using System.Data;
using Npgsql;

namespace Altinn.Apps.Monitoring.Application.Db;

internal enum DistributedLockName : long
{
    DbMigrator = 1,
    DbSeeder = 2,
    Application = 3,
}

internal sealed class DistributedLocking(
    ILogger<DistributedLocking> logger,
    [FromKeyedServices(Config.UserMode)] NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    Telemetry telemetry
)
{
    private readonly ILogger<DistributedLocking> _logger = logger;
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly Telemetry _telemetry = telemetry;

    public async ValueTask<IDistributedLockHandle> AcquireLock(
        DistributedLockName lockName,
        CancellationToken cancellationToken
    )
    {
        using var activity = _telemetry.Activities.StartActivity("DistributedLocking.AcquireLock");
        activity?.SetTag("lock.name", lockName.ToString());

        while (true)
        {
            var handle = await TryAcquireLock(lockName, cancellationToken);
            if (handle is not null)
                return handle;

            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken);
        }
    }

    public async ValueTask<IDistributedLockHandle?> TryAcquireLock(
        DistributedLockName lockName,
        CancellationToken cancellationToken
    )
    {
        using var activity = _telemetry.Activities.StartActivity("DistributedLocking.TryAcquireLock");
        activity?.SetTag("lock.name", lockName.ToString());

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@lockName);";
            cmd.Parameters.AddWithValue("lockName", (long)lockName);
            await cmd.PrepareAsync(cancellationToken);
            var hasLockObj = await cmd.ExecuteScalarAsync(cancellationToken);
            if (hasLockObj is not bool hasLock)
            {
                _logger.LogError("Unexpected result from acquiring lock: {ResultType}", hasLockObj?.GetType().Name);
                throw new Exception("Couldn't acquire lock");
            }

            if (!hasLock)
            {
                _logger.LogInformation("Failed to acquire lock {LockName}", lockName);
                return null;
            }

            var handle = new DistributedLockHandle(this, lockName, connection);
            connection = null;
            handle.Monitor();
            activity?.SetTag("lock.acquired", true);
            _logger.LogInformation("Acquired lock {LockName}", lockName);
            return handle;
        }
        finally
        {
            if (connection is not null)
            {
                activity?.SetTag("lock.acquired", false);
                await connection.DisposeAsync();
            }
        }
    }

    internal interface IDistributedLockHandle : IAsyncDisposable
    {
        // CancellationToken HandleLostToken { get; }
    }

    private sealed class DistributedLockHandle(
        DistributedLocking parent,
        DistributedLockName lockName,
        NpgsqlConnection connection
    ) : IDistributedLockHandle
    {
        private readonly DistributedLocking _parent = parent;
        private readonly DistributedLockName _lockName = lockName;
        private readonly NpgsqlConnection _connection = connection;
        private readonly CancellationTokenSource _cts = new();
        private SemaphoreSlim? _lock = new(1, 1);

        public void Monitor()
        {
            _connection.StateChange += (_, e) =>
            {
                StateChange(e.OriginalState, e.CurrentState);
            };
            _ = Task.Run(async () =>
            {
                var cancellationToken = _cts.Token;
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var lck = _lock;
                        if (lck is null)
                            break;

                        await lck.WaitAsync(cancellationToken);
                        try
                        {
                            await using var command = _connection.CreateCommand();
                            command.CommandText = """
                                SELECT COUNT(*)
                                FROM pg_locks
                                WHERE locktype = 'advisory'
                                    AND ((classid::bigint << 32) | objid::bigint) = @lockName
                                    AND pid = pg_backend_pid()
                                    AND granted = true;
                            """;
                            command.Parameters.AddWithValue("lockName", (long)_lockName);
                            await command.PrepareAsync(cancellationToken);
                            var lockCountObj = await command.ExecuteScalarAsync(cancellationToken);
                            if (lockCountObj is not long lockCount)
                            {
                                _parent._logger.LogError(
                                    "Unexpected result from monitoring lock: {ResultType}",
                                    lockCountObj?.GetType().Name
                                );
                                throw new Exception("Couldn't monitor lock");
                            }

                            if (cancellationToken.IsCancellationRequested)
                                break;

                            if (lockCount != 1)
                            {
                                _parent._logger.LogWarning(
                                    "Lock was lost? LockName={LockName}, Result={Result}",
                                    _lockName,
                                    lockCount
                                );
                            }
                            else
                            {
                                _parent._logger.LogInformation(
                                    "Monitored for lock: LockName={LockName}, Result={Result}",
                                    _lockName,
                                    lockCount
                                );
                            }
                        }
                        finally
                        {
                            lck.Release();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) when (_lock is null) { }
                    catch (Exception ex)
                    {
                        _parent._logger.LogError(ex, "Failed to monitor lock {LockName}", _lockName);
                    }

                    if (!cancellationToken.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromSeconds(30), _parent._timeProvider, cancellationToken);
                }
            });
        }

        public void StateChange(ConnectionState fromState, ConnectionState toState)
        {
            if (!_cts.Token.IsCancellationRequested)
            {
                _parent._logger.LogWarning(
                    "Distributed lock ({LockName}) connection state changed: {FromState}->{ToState}",
                    _lockName,
                    fromState,
                    toState
                );
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_cts.IsCancellationRequested)
                return;

            using var activity = _parent._telemetry.Activities.StartActivity("DistributedLocking.ReleaseLock");
            activity?.SetTag("lock.name", _lockName.ToString());

            if (_lock is null)
                throw new Exception("Lock is already disposed (this should not happen)");

            await _lock.WaitAsync();
            try
            {
                await _cts.CancelAsync();

                if (_connection.State != ConnectionState.Open)
                {
                    _parent._logger.LogWarning("Connection is not open when disposing lock handle");
                    return;
                }

                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(@lockName);";
                cmd.Parameters.AddWithValue("lockName", (long)_lockName);
                await cmd.PrepareAsync();
                var wasReleasedObj = await cmd.ExecuteScalarAsync();
                if (wasReleasedObj is not bool wasReleased)
                {
                    _parent._logger.LogError(
                        "Unexpected result from releasing lock: {ResultType}",
                        wasReleasedObj?.GetType().Name
                    );
                    throw new Exception("Couldn't release lock");
                }
                if (!wasReleased)
                {
                    _parent._logger.LogWarning("Failed to release lock {LockName} (it was not held)", _lockName);
                    throw new Exception("Couldn't release lock");
                }

                _parent._logger.LogInformation("Released lock {LockName}", _lockName);
            }
            finally
            {
                try
                {
                    await _connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _parent._logger.LogError(ex, "Failed to dispose connection when releasing lock");
                }
                _lock.Release();
                _cts.Dispose();
                var lck = _lock;
                _lock = null;
                lck.Dispose();
            }
        }
    }
}
