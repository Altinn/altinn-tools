using Medallion.Threading.Postgres;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application.Db;

internal enum DistributedLockName : long
{
    DbMigrator,
    DbSeeder,
    Orchestrator,
    Alerter,
}

internal sealed class DistributedLocking(ILogger<DistributedLocking> logger, IOptionsMonitor<AppConfiguration> config)
{
    private readonly ILogger<DistributedLocking> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _config = config;

    public async ValueTask<IDistributedLockHandle> Lock(
        DistributedLockName lockName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation("Acquiring lock {LockName}", lockName);
        var config = _config.CurrentValue;
        var connectionString = config.DbConnectionString;
        var @lock = new PostgresDistributedLock(
            new PostgresAdvisoryLockKey((long)lockName),
            connectionString,
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

    public interface IDistributedLockHandle : IAsyncDisposable
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
