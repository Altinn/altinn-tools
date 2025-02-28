using System.Data;
using System.Reflection;
using Altinn.Apps.Monitoring.Application.Db;
using DbUp;
using DbUp.Engine;

namespace Altinn.Apps.Monitoring.Application.DbUp;

internal sealed class Migrator(
    ILogger<Migrator> logger,
    DistributedLocking locking,
    [FromKeyedServices(Config.AdminMode)] ConnectionString connectionString
) : IHostedService
{
    private readonly ILogger<Migrator> _logger = logger;
    private readonly DistributedLocking _locking = locking;
    private readonly ConnectionString _connectionString = connectionString;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var _ = await _locking.Lock(DistributedLockName.DbMigrator, cancellationToken);

            var upgrader = DeployChanges
                .To.PostgresqlDatabase(_connectionString.Value)
                .JournalToPostgresqlTable(Repository.Schema, "schema_version")
                .WithScriptsAndCodeEmbeddedInAssembly(Assembly.GetExecutingAssembly())
                .LogTo(_logger)
                .WithTransaction()
                .Build();

            var result = upgrader.PerformUpgrade();

            if (!result.Successful)
            {
                _logger.LogError(
                    result.Error,
                    "Failed to upgrade database, using script: {ErrorScript}",
                    result.ErrorScript.Name
                );
                throw new Exception("Failed to upgrade database", result.Error);
            }

            foreach (var script in result.Scripts)
                _logger.LogInformation("Executed script: {Script}", script.Name);

            _logger.LogInformation("Database upgrade successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed seeding database");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class Script0001Initial : IScript
{
    public string ProvideScript(Func<IDbCommand> dbCommandFactory)
    {
        return $"""
                CREATE TABLE {Repository.Tables.Telemetry} (
                    id BIGSERIAL PRIMARY KEY,
                    ext_id TEXT NOT NULL,
                    service_owner TEXT NOT NULL,
                    app_name TEXT NOT NULL,
                    app_version TEXT NOT NULL,
                    time_generated TIMESTAMPTZ NOT NULL,
                    time_ingested TIMESTAMPTZ NOT NULL,
                    dupe_count BIGINT NOT NULL,
                    seeded BOOLEAN NOT NULL,
                    data JSONB NOT NULL,
                    UNIQUE (service_owner, ext_id)
                );

                CREATE INDEX idx_telemetry_time_generated ON {Repository.Tables.Telemetry} (time_generated);
                CREATE INDEX idx_telemetry_seeded ON {Repository.Tables.Telemetry} (seeded);

                CREATE TABLE {Repository.Tables.Queries} (
                    id BIGSERIAL PRIMARY KEY,
                    service_owner TEXT NOT NULL,
                    name TEXT NOT NULL,
                    hash TEXT NOT NULL,
                    queried_until TIMESTAMPTZ NOT NULL,
                    UNIQUE (service_owner, hash)
                );

                CREATE TABLE {Repository.Tables.Alerts} (
                    id BIGSERIAL PRIMARY KEY,
                    state INTEGER NOT NULL,
                    telemetry_id BIGSERIAL NOT NULL REFERENCES {Repository.Tables.Telemetry} (id),
                    data JSONB NOT NULL,
                    UNIQUE (telemetry_id)
                );

                CREATE INDEX idx_alerts_from_telemetry ON {Repository.Tables.Alerts} (telemetry_id, state, (data->>'$type'));
            """;
    }
}
