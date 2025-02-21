using Altinn.Apps.Monitoring.Domain;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using SQLite;

namespace Altinn.Apps.Monitoring.Application.Db;

internal sealed class Seeder(
    ILogger<Seeder> logger,
    IOptions<AppConfiguration> config,
    Repository repository,
    NpgsqlDataSource dataSource,
    DistributedLocking locking
) : IHostedService
{
    private readonly ILogger<Seeder> _logger = logger;
    private readonly Repository _repository = repository;
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly AppConfiguration _config = config.Value;
    private readonly DistributedLocking _locking = locking;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var handle = await _locking.Lock(DistributedLockName.DbSeeder, cancellationToken);

            if (_config.DisableSeeder)
            {
                _logger.LogInformation("Seeder disabled");
                return;
            }

            var alreadyHasData = await _repository.HasAnyTelemetry(cancellationToken);
            if (alreadyHasData)
            {
                _logger.LogInformation("Database already has data, can only seed empty database");
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.SeedSqliteDbPath))
                throw new InvalidOperationException("SeedSqliteDbPath configuration is required for seeding");

            var dbFileInfo = new FileInfo(_config.SeedSqliteDbPath);
            if (!dbFileInfo.Exists)
                throw new FileNotFoundException("SQLite db does not exist", dbFileInfo.FullName);

            var sourceDb = new SQLiteAsyncConnection(dbFileInfo.FullName);
            try
            {
                var records = await sourceDb.Table<ErrorRecord>().ToArrayAsync();
                if (records.Length == 0)
                    throw new InvalidOperationException("No records found in source SQLite database for seeding");

                var entities = new TelemetryEntity[records.Length];

                for (int i = 0; i < records.Length; i++)
                {
                    var record = records[i];
                    if (string.IsNullOrWhiteSpace(record.ServiceOwner))
                        throw new InvalidOperationException("ServiceOwner is required for seeding traces");
                    if (string.IsNullOrWhiteSpace(record.AppRoleName))
                        throw new InvalidOperationException("AppRoleName is required for seeding traces");
                    if (string.IsNullOrWhiteSpace(record.AppVersion))
                        throw new InvalidOperationException("AppVersion is required for seeding traces");
                    if (string.IsNullOrWhiteSpace(record.OperationId))
                        throw new InvalidOperationException("OperationId is required for seeding traces");
                    if (string.IsNullOrWhiteSpace(record.Id))
                        throw new InvalidOperationException("Id is required for seeding traces");
                    if (string.IsNullOrWhiteSpace(record.OperationName))
                        throw new InvalidOperationException("OperationName is required for seeding traces");
                    if (string.IsNullOrWhiteSpace(record.Name))
                        throw new InvalidOperationException("Name is required for seeding traces");
                    if (record.DurationMs is null)
                        throw new InvalidOperationException("DurationMs is required for seeding traces");

                    entities[i] = new TelemetryEntity
                    {
                        Id = 0,
                        ExtId = $"{record.OperationId}-{record.Id}",
                        ServiceOwner = ServiceOwner.Parse(record.ServiceOwner).Value,
                        AppName = record.AppRoleName,
                        AppVersion = record.AppVersion,
                        TimeGenerated = Instant.FromDateTimeOffset(record.TimeGenerated),
                        TimeIngested = Instant.FromDateTimeOffset(record.TimeIngested),
                        DupeCount = 0,
                        Seeded = true,
                        Data = new TraceData
                        {
                            AltinnErrorId = record.ErrorNumber,
                            InstanceOwnerPartyId = record.InstanceOwnerPartyId,
                            InstanceId = record.InstanceId,
                            TraceId = record.OperationId,
                            SpanId = record.Id,
                            ParentSpanId = record.ParentId,
                            TraceName = record.OperationName,
                            SpanName = record.Name,
                            Success = record.Success,
                            Result = record.ResultCode,
                            Duration = Duration.FromMilliseconds(record.DurationMs.Value),
                            Attributes = new()
                            {
                                { "Target", record.Target },
                                { "DependencyType", record.DependencyType },
                                { "Data", record.Data },
                                { "PerformanceBucket", record.PerformanceBucket },
                                { "Properties", record.Properties },
                            },
                        },
                    };
                }

                _logger.LogInformation("Seeding database with {Count} trace records", entities.Length);

                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var import = connection.BeginBinaryImport(
                    "COPY monitoring.telemetry (ext_id, service_owner, app_name, app_version, time_generated, time_ingested, dupe_count, seeded, data) FROM STDIN (FORMAT binary)"
                );
                for (int i = 0; i < entities.Length; i++)
                {
                    var item = entities[i];

                    await import.StartRowAsync(cancellationToken);
                    await import.WriteAsync(item.ExtId, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.ServiceOwner, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.AppName, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.AppVersion, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.TimeGenerated, NpgsqlDbType.TimestampTz, cancellationToken);
                    await import.WriteAsync(item.TimeIngested, NpgsqlDbType.TimestampTz, cancellationToken);
                    await import.WriteAsync(item.DupeCount, NpgsqlDbType.Bigint, cancellationToken);
                    await import.WriteAsync(item.Seeded, NpgsqlDbType.Boolean, cancellationToken);
                    await import.WriteAsync(item.Data, NpgsqlDbType.Jsonb, cancellationToken);
                }

                await import.CompleteAsync(cancellationToken);

                _logger.LogInformation("Seeding database with {Count} trace records completed", entities.Length);
            }
            finally
            {
                await sourceDb.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
                return;

            _completion.TrySetException(ex);
            _logger.LogError(ex, "Failed seeding database");
            throw;
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public sealed class ErrorRecord
    {
        [PrimaryKey, AutoIncrement]
        public int PK { get; set; }

        [Indexed]
        public string? ServiceOwner { get; init; }

        [Indexed]
        public DateTimeOffset TimeGenerated { get; init; }

        [Indexed]
        public DateTimeOffset TimeIngested { get; init; }

        [Indexed]
        public int? InstanceOwnerPartyId { get; init; }

        [Indexed]
        public Guid? InstanceId { get; init; }

        [Indexed]
        public string? Id { get; init; }
        public string? Target { get; init; }
        public string? DependencyType { get; init; }
        public string? Name { get; init; }
        public string? Data { get; init; }

        [Indexed]
        public bool? Success { get; init; }

        [Indexed]
        public string? ResultCode { get; init; }
        public double? DurationMs { get; init; }
        public string? PerformanceBucket { get; init; }
        public string? Properties { get; init; }
        public string? OperationName { get; init; }

        [Indexed]
        public string? OperationId { get; init; }
        public string? ParentId { get; init; }

        [Indexed]
        public string? AppVersion { get; init; }

        [Indexed]
        public string? AppRoleName { get; init; }

        [Indexed]
        public bool AlertedInSlack { get; set; }

        [Indexed]
        public int ErrorNumber { get; set; }
    }
}
