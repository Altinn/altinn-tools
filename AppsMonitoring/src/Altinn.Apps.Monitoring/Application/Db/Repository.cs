using Altinn.Apps.Monitoring.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Altinn.Apps.Monitoring.Application.Db;

// Implementation notes:
// * We use non-async `GetFieldValue<>` because we don't use `CommandBehavior.SequentialAccess`,
//   so columns/values are buffered when the row read
#pragma warning disable CA1849 // Call async methods when in an async method

internal sealed record InsertTelemetryResult(int Written, IReadOnlyList<long> Ids, IReadOnlySet<string> DupeExtIds);

internal sealed class Repository(
    ILogger<Repository> logger,
    [FromKeyedServices(Config.UserMode)] NpgsqlDataSource userDataSource,
    [FromKeyedServices(Config.AdminMode)] NpgsqlDataSource adminDataSource
)
{
    public const string Schema = "monitor";

    internal static class Tables
    {
        internal const string Telemetry = $"{Schema}.telemetry";
        internal const string Queries = $"{Schema}.queries";
        internal const string Alerts = $"{Schema}.alerts";

        // Not owned by this app, just read. Contains the SQLite db seed
        internal const string Seed = "seed";

        internal static readonly string[] All = { Telemetry, Queries, Alerts };
    }

    private readonly ILogger<Repository> _logger = logger;
    private readonly NpgsqlDataSource _userDataSource = userDataSource;
    private readonly NpgsqlDataSource _adminDataSource = adminDataSource;

    public async ValueTask<byte[]?> GetSqliteSeed(CancellationToken cancellationToken)
    {
        await using var connection = await _adminDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT data FROM {Tables.Seed} ORDER BY id DESC LIMIT 1";

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result as byte[];
    }

    public async ValueTask<IReadOnlyList<QueryStateEntity>> ListQueryStates(
        ServiceOwner? serviceOwner = null,
        Query? query = null,
        CancellationToken cancellationToken = default
    )
    {
        if (query is not null && serviceOwner is null)
            throw new ArgumentException("Service owner must be specified when querying by query hash");

        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Tables.Queries}";
        if (serviceOwner is not null)
        {
            command.CommandText += " WHERE service_owner = @service_owner";
            command.Parameters.AddWithValue("service_owner", serviceOwner.Value.Value);

            if (query is not null)
            {
                command.CommandText += " AND hash = @hash";
                command.Parameters.AddWithValue("hash", query.Hash);
            }
        }

        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var queryStates = new List<QueryStateEntity>(16);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new QueryStateEntity
            {
                Id = reader.GetFieldValue<long>(0),
                ServiceOwner = reader.GetFieldValue<string>(1),
                Name = reader.GetFieldValue<string>(2),
                Hash = reader.GetFieldValue<string>(3),
                QueriedUntil = reader.GetFieldValue<Instant>(4),
            };

            queryStates.Add(item);
        }

        return queryStates;
    }

    public async ValueTask<bool> HasAnyTelemetry(CancellationToken cancellationToken)
    {
        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Tables.Telemetry}";
        await command.PrepareAsync(cancellationToken);
        var countObj = await command.ExecuteScalarAsync(cancellationToken);
        if (countObj is not long count)
            throw new InvalidOperationException("Unexpected result from COUNT(*) query: " + countObj);
        return count > 0;
    }

    private static TelemetryEntity ReadTelemetryEntity(NpgsqlDataReader reader)
    {
        return new TelemetryEntity
        {
            Id = reader.GetFieldValue<long>(0),
            ExtId = reader.GetFieldValue<string>(1),
            ServiceOwner = reader.GetFieldValue<string>(2),
            AppName = reader.GetFieldValue<string>(3),
            AppVersion = reader.GetFieldValue<string>(4),
            TimeGenerated = reader.GetFieldValue<Instant>(5),
            TimeIngested = reader.GetFieldValue<Instant>(6),
            DupeCount = reader.GetFieldValue<long>(7),
            Seeded = reader.GetFieldValue<bool>(8),
            Data = reader.GetFieldValue<TelemetryData>(9),
        };
    }

    public async ValueTask<IReadOnlyList<TelemetryEntity>> ListTelemetry(
        ServiceOwner? serviceOwner = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Tables.Telemetry}";
        if (serviceOwner is not null)
        {
            command.CommandText += " WHERE service_owner = @service_owner";
            command.Parameters.AddWithValue("service_owner", serviceOwner.Value);
        }

        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var telemetry = new List<TelemetryEntity>(16);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = ReadTelemetryEntity(reader);

            telemetry.Add(item);
        }

        return telemetry;
    }

    public async ValueTask<InsertTelemetryResult> InsertTelemetry(
        ServiceOwner serviceOwner,
        Query query,
        Instant searchTo,
        IReadOnlyList<TelemetryEntity> telemetry,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int written = 0;
        HashSet<string> dupes = [];
        var ids = new List<long>(telemetry.Count);

        if (telemetry.Count > 0)
        {
            var ingestionTimestamp = telemetry[0].TimeIngested;
            if (ingestionTimestamp == Instant.MinValue)
                throw new InvalidOperationException("Telemetry must have a valid ingestion timestamp");

            // 1. Creat temp table (ON COMMIT DROP)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                        CREATE TEMP TABLE telemetry_import (
                            ext_id TEXT NOT NULL,
                            service_owner TEXT NOT NULL,
                            app_name TEXT NOT NULL,
                            app_version TEXT NOT NULL,
                            time_generated TIMESTAMPTZ NOT NULL,
                            time_ingested TIMESTAMPTZ NOT NULL,
                            seeded BOOLEAN NOT NULL,
                            data JSONB NOT NULL
                        ) ON COMMIT DROP;
                    """;
                await command.PrepareAsync(cancellationToken);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // 2. Copy data into temp table
            {
                await using var import = connection.BeginBinaryImport(
                    "COPY telemetry_import (ext_id, service_owner, app_name, app_version, time_generated, time_ingested, seeded, data) FROM STDIN (FORMAT binary)"
                );
                for (int i = 0; i < telemetry.Count; i++)
                {
                    var item = telemetry[i];
                    if (item.TimeIngested != ingestionTimestamp)
                        throw new InvalidOperationException(
                            "All telemetry items must have the same ingestion timestamp"
                        );

                    await import.StartRowAsync(cancellationToken);
                    await import.WriteAsync(item.ExtId, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.ServiceOwner, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.AppName, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.AppVersion, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.TimeGenerated, NpgsqlDbType.TimestampTz, cancellationToken);
                    await import.WriteAsync(item.TimeIngested, NpgsqlDbType.TimestampTz, cancellationToken);
                    await import.WriteAsync(item.Seeded, NpgsqlDbType.Boolean, cancellationToken);
                    await import.WriteAsync(item.Data, NpgsqlDbType.Jsonb, cancellationToken);
                }

                await import.CompleteAsync(cancellationToken);
            }

            // 3. Insert data from temp table into telemetry table, with conflict resolution (do nothing)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                        INSERT INTO {Tables.Telemetry} (ext_id, service_owner, app_name, app_version, time_generated, time_ingested, dupe_count, seeded, data)
                        SELECT ext_id, service_owner, app_name, app_version, time_generated, time_ingested, 0 as dupe_count, seeded, data FROM telemetry_import
                        ON CONFLICT (service_owner, ext_id) DO UPDATE SET dupe_count = {Tables.Telemetry}.dupe_count + 1
                        RETURNING id, ext_id, time_ingested;
                    """;
                await command.PrepareAsync(cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetFieldValue<long>(0);
                    var extId = reader.GetFieldValue<string>(1);
                    var timeIngested = reader.GetFieldValue<Instant>(2);
                    ids.Add(id);
                    if (timeIngested != ingestionTimestamp)
                        dupes.Add(extId);
                }
                written = telemetry.Count - dupes.Count;

                _logger.LogInformation(
                    "Inserted {Written}/{Total} rows into telemetry table, found {DupeCount} duplicates",
                    written,
                    telemetry.Count,
                    dupes.Count
                );
            }
        }

        // 4. update queries table with 'queried_until'
        {
            var to = written > 0 ? telemetry.Where(t => !dupes.Contains(t.ExtId)).Max(t => t.TimeGenerated) : searchTo;
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                    INSERT INTO {Tables.Queries} (service_owner, name, hash, queried_until)
                    VALUES (@service_owner, @name, @hash, @queried_until)
                    ON CONFLICT (service_owner, hash) DO UPDATE SET name = EXCLUDED.name, queried_until = EXCLUDED.queried_until;
                """;
            command.Parameters.AddWithValue("service_owner", serviceOwner.Value);
            command.Parameters.AddWithValue("name", query.Name);
            command.Parameters.AddWithValue("hash", query.Hash);
            command.Parameters.AddWithValue("queried_until", to);

            await command.PrepareAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new InsertTelemetryResult(written, ids, dupes);
    }

    public async ValueTask<int> SeedTelemetry(
        IReadOnlyList<TelemetryEntity> telemetry,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var import = connection.BeginBinaryImport(
            $"COPY {Tables.Telemetry} (ext_id, service_owner, app_name, app_version, time_generated, time_ingested, dupe_count, seeded, data) FROM STDIN (FORMAT binary)"
        );
        for (int i = 0; i < telemetry.Count; i++)
        {
            var item = telemetry[i];

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

        return (int)await import.CompleteAsync(cancellationToken);
    }

    public async ValueTask<(TelemetryEntity Telemetry, AlertEntity? Alert)[]> ListAlerterWorkItems(
        string type,
        CancellationToken cancellationToken
    )
    {
        if (AlertData.TypeIsValid(type) is false)
            throw new ArgumentException("Invalid alert type", nameof(type));

        // We want
        // * Telemetry items that don't have alerts
        // * Telemetry items that have alerts that are < Mitigated

        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                SELECT
                    t.id as telemetry_id, t.ext_id as telemetry_ext_id, t.service_owner, t.app_name, t.app_version, t.time_generated, t.time_ingested, t.dupe_count, t.seeded, t.data,
                    a.id as alert_id, a.state as alert_state, a.data as alert_data, a.created_at as alert_created_at, a.updated_at as alert_updated_at
                FROM {Tables.Telemetry} t
                LEFT JOIN {Tables.Alerts} a ON t.id = a.telemetry_id
                WHERE t.seeded = FALSE AND (a.id IS NULL OR (a.state < @state AND a.data->>'$type' = @type));
            """;
        //  For now we only care about pending alerts, since we don't have a mitigation flow yet
        command.Parameters.AddWithValue("state", (int)AlertState.Alerted);
        command.Parameters.AddWithValue("type", type);

        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var workItems = new List<(TelemetryEntity Telemetry, AlertEntity? Alert)>(16);

        while (await reader.ReadAsync(cancellationToken))
        {
            var telemetry = ReadTelemetryEntity(reader);
            var alertId = reader.IsDBNull(10) ? (long?)null : reader.GetFieldValue<long>(10);
            var alert = alertId is null
                ? null
                : new AlertEntity
                {
                    Id = alertId.Value,
                    State = (AlertState)reader.GetFieldValue<int>(11),
                    TelemetryId = telemetry.Id,
                    Data = reader.GetFieldValue<AlertData>(12),
                    CreatedAt = reader.GetFieldValue<Instant>(13),
                    UpdatedAt = reader.GetFieldValue<Instant>(14),
                };

            if (alert is not null && !alert.Data.IsType(type))
                throw new InvalidOperationException("Unexpected alert type: " + alert.Data.GetType().Name);

            workItems.Add((telemetry, alert));
        }

        return workItems.ToArray();
    }

    public async ValueTask<IReadOnlyList<AlertEntity>> ListAlerts(CancellationToken cancellationToken)
    {
        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Tables.Alerts}";
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var alerts = new List<AlertEntity>(16);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new AlertEntity
            {
                Id = reader.GetFieldValue<long>(0),
                State = (AlertState)reader.GetFieldValue<int>(1),
                TelemetryId = reader.GetFieldValue<long>(2),
                Data = reader.GetFieldValue<AlertData>(3),
                CreatedAt = reader.GetFieldValue<Instant>(4),
                UpdatedAt = reader.GetFieldValue<Instant>(5),
            };

            alerts.Add(item);
        }

        return alerts;
    }

    public async ValueTask SaveAlert(AlertEntity alert, CancellationToken cancellationToken)
    {
        await using var connection = await _userDataSource.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                INSERT INTO {Tables.Alerts} (state, telemetry_id, data, created_at, updated_at)
                VALUES (@state, @telemetry_id, @data, @created_at, @updated_at)
                ON CONFLICT (telemetry_id) DO
                UPDATE SET state = EXCLUDED.state, data = EXCLUDED.data, updated_at = EXCLUDED.updated_at
            """;
        command.Parameters.AddWithValue("state", (int)alert.State);
        command.Parameters.AddWithValue("telemetry_id", alert.TelemetryId);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, alert.Data);
        command.Parameters.AddWithValue("created_at", alert.CreatedAt);
        command.Parameters.AddWithValue("updated_at", alert.UpdatedAt);

        await command.PrepareAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<IndexRecommendation>> ListIndexRecommendations(
        CancellationToken cancellationToken
    )
    {
        foreach (var table in Tables.All)
        {
            // Vacuum and analyze
            await using var connection = await _adminDataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
            command.CommandText = $"VACUUM FULL ANALYZE {table}";
#pragma warning restore CA2100 // Review SQL queries for security vulnerabilities
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Wait for 'pg_stat_all_tables' to update
        // Not sure if there are better ways..
        // It's OK for this to take some time as all the tests are taking atleast >10s
        await Task.Delay(5000, cancellationToken);

        {
            // Query for recommendations
            await using var connection = await _adminDataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = $"""
                    SELECT
                        relname as table_name,
                        seq_scan-idx_scan AS table_too_much_seq,
                        case when seq_scan-idx_scan>0 THEN 'Missing Index?' ELSE 'OK' END as table_result,
                        pg_relation_size(relid::regclass) AS table_rel_size,
                        seq_scan as total_seq_scan,
                        idx_scan as total_index_scan
                    FROM pg_stat_all_tables
                    WHERE schemaname='{Schema}'
                    ORDER BY table_too_much_seq DESC NULLS LAST;
                """;

            await command.PrepareAsync(cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            List<IndexRecommendation> recommendations = new();
            while (await reader.ReadAsync(cancellationToken))
            {
                var tableName = reader.GetFieldValue<string>(0);
                var tooMuchSeq = reader.GetFieldValue<long>(1);
                var tableResult = reader.GetFieldValue<string>(2);
                var tableSize = reader.GetFieldValue<long>(3);
                var totalSeqScan = reader.GetFieldValue<long>(4);
                var totalIndexScan = reader.GetFieldValue<long>(5);

                recommendations.Add(
                    new IndexRecommendation(tableName, tooMuchSeq, tableResult, tableSize, totalSeqScan, totalIndexScan)
                );
            }

            return recommendations;
        }
    }
}
