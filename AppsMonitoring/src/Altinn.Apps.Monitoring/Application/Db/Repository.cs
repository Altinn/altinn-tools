using Altinn.Apps.Monitoring.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Altinn.Apps.Monitoring.Application.Db;

internal sealed class Repository(ILogger<Repository> logger, NpgsqlDataSource dataSource)
{
    private readonly ILogger<Repository> _logger = logger;
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async ValueTask<IReadOnlyList<QueryStateEntity>> ListQueryStates(
        ServiceOwner? serviceOwner = null,
        Query? query = null,
        CancellationToken cancellationToken = default
    )
    {
        if (query is not null && serviceOwner is null)
            throw new ArgumentException("Service owner must be specified when querying by query hash");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                SELECT *
                FROM monitoring.queries
            """;
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
                Id = reader.GetInt64(0),
                ServiceOwner = reader.GetString(1),
                Name = reader.GetString(2),
                Hash = reader.GetString(3),
                QueriedUntil = reader.GetFieldValue<Instant>(4),
            };

            queryStates.Add(item);
        }

        return queryStates;
    }

    public async ValueTask<bool> HasAnyTelemetry(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitoring.telemetry";
        await command.PrepareAsync(cancellationToken);
        var countObj = await command.ExecuteScalarAsync(cancellationToken);
        if (countObj is not long count)
            throw new InvalidOperationException("Unexpected result from COUNT(*) query: " + countObj);
        return count > 0;
    }

    private async ValueTask<TelemetryEntity> ReadTelemetryEntity(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken
    )
    {
        return new TelemetryEntity
        {
            Id = reader.GetInt64(0),
            ExtId = reader.GetString(1),
            ServiceOwner = reader.GetString(2),
            AppName = reader.GetString(3),
            AppVersion = reader.GetString(4),
            TimeGenerated = reader.GetFieldValue<Instant>(5),
            TimeIngested = reader.GetFieldValue<Instant>(6),
            DupeCount = reader.GetInt64(7),
            Seeded = reader.GetBoolean(8),
            Data = reader.GetFieldValue<TelemetryData>(9),
        };
    }

    public async ValueTask<IReadOnlyList<TelemetryEntity>> ListTelemetry(
        ServiceOwner? serviceOwner = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM monitoring.telemetry";
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
            var item = await ReadTelemetryEntity(reader, cancellationToken);

            telemetry.Add(item);
        }

        return telemetry;
    }

    public async ValueTask<int> InsertTelemetry(
        ServiceOwner serviceOwner,
        Query query,
        Instant searchTo,
        IReadOnlyList<TelemetryEntity> telemetry,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int written = 0;

        HashSet<string> dupes = [];
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
                command.CommandText = """
                        INSERT INTO monitoring.telemetry (ext_id, service_owner, app_name, app_version, time_generated, time_ingested, dupe_count, seeded, data)
                        SELECT ext_id, service_owner, app_name, app_version, time_generated, time_ingested, 0 as dupe_count, seeded, data FROM telemetry_import
                        ON CONFLICT (service_owner, ext_id) DO UPDATE SET dupe_count = monitoring.telemetry.dupe_count + 1
                        RETURNING ext_id, time_ingested;
                    """;
                await command.PrepareAsync(cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var extId = reader.GetString(0);
                    var timeIngested = reader.GetFieldValue<Instant>(1);
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
            command.CommandText = """
                    INSERT INTO monitoring.queries (service_owner, name, hash, queried_until)
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
        return written;
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
        // * Telemetry items that have alerts that are < Mitigated (TODO: handle mitigation)

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                SELECT
                    t.id as telemetry_id, t.ext_id as telemetry_ext_id, t.service_owner, t.app_name, t.app_version, t.time_generated, t.time_ingested, t.dupe_count, t.seeded, t.data,
                    a.id as alert_id, a.state as alert_state, a.data as alert_data
                FROM monitoring.telemetry t
                LEFT JOIN monitoring.alerts a ON t.id = a.telemetry_id
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
            var telemetry = await ReadTelemetryEntity(reader, cancellationToken);
            var alertId = reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10);
            var alert = alertId is null
                ? null
                : new AlertEntity
                {
                    Id = alertId.Value,
                    State = (AlertState)reader.GetInt32(11),
                    TelemetryId = telemetry.Id,
                    Data = reader.GetFieldValue<AlertData>(12),
                };

            if (alert is not null && !alert.Data.IsType(type))
                throw new InvalidOperationException("Unexpected alert type: " + alert.Data.GetType().Name);

            workItems.Add((telemetry, alert));
        }

        return workItems.ToArray();
    }

    public async ValueTask<IReadOnlyList<AlertEntity>> ListAlerts(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM monitoring.alerts";
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var alerts = new List<AlertEntity>(16);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new AlertEntity
            {
                Id = reader.GetInt64(0),
                State = (AlertState)reader.GetInt32(1),
                TelemetryId = reader.GetInt64(2),
                Data = reader.GetFieldValue<AlertData>(3),
            };

            alerts.Add(item);
        }

        return alerts;
    }

    public async ValueTask SaveAlert(AlertEntity alert, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                INSERT INTO monitoring.alerts (state, telemetry_id, data)
                VALUES (@state, @telemetry_id, @data)
                ON CONFLICT (telemetry_id) DO UPDATE SET state = EXCLUDED.state, data = EXCLUDED.data
            """;
        command.Parameters.AddWithValue("state", (int)alert.State);
        command.Parameters.AddWithValue("telemetry_id", alert.TelemetryId);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, alert.Data);

        await command.PrepareAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
