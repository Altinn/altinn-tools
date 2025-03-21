using Medallion.Threading.Postgres;

namespace Altinn.Apps.Monitoring.Application.Db;

internal enum DistributedLockName : long
{
    DbMigrator = 1,
    DbSeeder = 2,
    Orchestrator = 3,
    Alerter = 4,
}

internal sealed class DistributedLocking(
    ILogger<DistributedLocking> logger,
    [FromKeyedServices(Config.UserMode)] ConnectionString connectionString,
    Telemetry telemetry
)
{
    private readonly ILogger<DistributedLocking> _logger = logger;
    private readonly ConnectionString _connectionString = connectionString;
    private readonly Telemetry _telemetry = telemetry;

    public async ValueTask<IDistributedLockHandle> AcquireLock(
        DistributedLockName lockName,
        CancellationToken cancellationToken
    )
    {
        using var activity = _telemetry.Activities.StartActivity("DistributedLocking.AcquireLock");
        activity?.SetTag("lock.name", lockName.ToString());

        var @lock = new PostgresDistributedLock(
            new PostgresAdvisoryLockKey((long)lockName),
            _connectionString.Value,
            options =>
            {
                options.KeepaliveCadence(TimeSpan.FromMinutes(1));
                options.UseMultiplexing(true);
            }
        );

        var innerHandle = await @lock.AcquireAsync(cancellationToken: cancellationToken);
        var handle = new DistributedLockHandle(innerHandle);
        _logger.LogInformation("Acquired lock {LockName}", lockName);
        return handle;
    }

    internal interface IDistributedLockHandle : IAsyncDisposable
    {
        CancellationToken HandleLostToken { get; }
    }

    private sealed class DistributedLockHandle : IDistributedLockHandle
    {
        private readonly PostgresDistributedLockHandle _handle;

        public DistributedLockHandle(PostgresDistributedLockHandle handle) => _handle = handle;

        public CancellationToken HandleLostToken => _handle.HandleLostToken;

        public ValueTask DisposeAsync() => _handle.DisposeAsync();
    }
}
