using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;
using Azure;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager.OperationalInsights;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed partial class AzureServiceOwnerMonitorAdapter(
    AzureClients clients,
    AzureServiceOwnerResources serviceOwnerResources,
    TimeProvider timeProvider,
    Telemetry telemetry
) : IServiceOwnerLogsAdapter, IServiceOwnerTraceAdapter, IServiceOwnerMetricsAdapter
{
    private readonly LogsQueryClient _logsClient = clients.LogsQueryClient;
    private readonly AzureServiceOwnerResources _serviceOwnerResources = serviceOwnerResources;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly Telemetry _telemetry = telemetry;

    public async ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    )
    {
        using var activity = _telemetry.Activities.StartActivity("AzureServiceOwnerMonitorAdapter.Query");
        activity?.SetTag("serviceowner", serviceOwner.Value);
        activity?.SetTag("query.name", query.Name);
        activity?.SetTag("query.from", from.ToString());
        activity?.SetTag("query.to", to.ToString());

        var resources = await _serviceOwnerResources.GetResources(serviceOwner, cancellationToken);
        if (resources is null)
            return [];

        var (_, _, workspace) = resources;

        switch (query.Type)
        {
            case QueryType.Traces:
                return await QueryTraces(serviceOwner, query, workspace, from, to, cancellationToken);
            case QueryType.Metrics:
                return await QueryMetrics(serviceOwner, query, workspace, from, to, cancellationToken);
            default:
                throw new NotSupportedException($"Query type {query.Type} not supported");
        }
    }

    private async ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> QueryTraces(
        ServiceOwner serviceOwner,
        Query query,
        OperationalInsightsWorkspaceResource workspace,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    )
    {
        var searchTimestamp = _timeProvider.GetCurrentInstant();

        Response<LogsQueryResult> results = await _logsClient.QueryResourceAsync(
            workspace.Id,
            query.Format(from, to),
            new QueryTimeRange((to - from).Plus(Duration.FromMinutes(5)).ToTimeSpan()),
            cancellationToken: cancellationToken
        );

        var tables = results?.Value?.AllTables;
        if (tables is null)
            return [];
        var totalRows = tables.Sum(table => table.Rows.Count);

        var telemetry = new List<IReadOnlyList<TelemetryEntity>>(totalRows);
        var instanceIdRegex = InstanceIdInUrlRegex();
        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (table.Rows.Count == 0)
                continue;

            telemetry.Add(ReadTraces(serviceOwner, i, table, instanceIdRegex));
        }

        return telemetry;
    }

    private async ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> QueryMetrics(
        ServiceOwner serviceOwner,
        Query query,
        OperationalInsightsWorkspaceResource workspace,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    )
    {
        var searchTimestamp = _timeProvider.GetCurrentInstant();

        Response<LogsQueryResult> results = await _logsClient.QueryResourceAsync(
            workspace.Id,
            query.Format(from, to),
            new QueryTimeRange((to - from).Plus(Duration.FromMinutes(5)).ToTimeSpan()),
            cancellationToken: cancellationToken
        );

        var tables = results?.Value?.AllTables;
        if (tables is null)
            return [];
        var totalRows = tables.Sum(table => table.Rows.Count);

        var telemetry = new List<IReadOnlyList<TelemetryEntity>>(totalRows);
        var instanceIdRegex = InstanceIdInUrlRegex();
        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (table.Rows.Count == 0)
                continue;

            telemetry.Add(ReadMetrics(serviceOwner, query, i, table));
        }

        return telemetry;
    }

    static List<TelemetryEntity> ReadTraces(ServiceOwner serviceOwner, int i, LogsTable table, Regex instanceIdRegex)
    {
        var telemetry = new List<TelemetryEntity>(table.Rows.Count);

        var indexes = table.Columns.Index();
        int nameIdx = -1;
        int operationNameIdx = -1;
        int operationIdIdx = -1;
        int urlIdx = -1;
        int timeGeneratedIdx = -1;
        int idIdx = -1;
        int targetIdx = -1;
        int dependencyTypeIdx = -1;
        int dataIdx = -1;
        int successIdx = -1;
        int resultCodeIdx = -1;
        int durationIdx = -1;
        int appRoleNameIdx = -1;
        int appVersionIdx = -1;
        int parentIdIdx = -1;
        int performanceBucketIdx = -1;
        int propertiesIdx = -1;

        foreach (var (rowIndex, column) in indexes)
        {
            if (column.Name == "Name")
                nameIdx = rowIndex;
            else if (column.Name == "OperationName")
                operationNameIdx = rowIndex;
            else if (column.Name == "OperationId")
                operationIdIdx = rowIndex;
            else if (column.Name == "TimeGenerated")
                timeGeneratedIdx = rowIndex;
            else if (column.Name == "Id")
                idIdx = rowIndex;
            else if (column.Name == "Target")
                targetIdx = rowIndex;
            else if (column.Name == "DependencyType")
                dependencyTypeIdx = rowIndex;
            else if (column.Name == "Data")
                dataIdx = rowIndex;
            else if (column.Name == "Success")
                successIdx = rowIndex;
            else if (column.Name == "ResultCode")
                resultCodeIdx = rowIndex;
            else if (column.Name == "DurationMs")
                durationIdx = rowIndex;
            else if (column.Name == "Url")
                urlIdx = rowIndex;
            else if (column.Name == "AppRoleName")
                appRoleNameIdx = rowIndex;
            else if (column.Name == "AppVersion")
                appVersionIdx = rowIndex;
            else if (column.Name == "ParentId")
                parentIdIdx = rowIndex;
            else if (column.Name == "PerformanceBucket")
                performanceBucketIdx = rowIndex;
            else if (column.Name == "Properties")
                propertiesIdx = rowIndex;
        }

        for (int j = 0; j < table.Rows.Count; j++)
        {
            var row = table.Rows[j];

            var name = ReadString(row, nameIdx);
            var rootTraceRequestUrl = ReadString(row, urlIdx);

            int? instanceOwnerPartyId = null;
            Guid? instanceId = null;
            var match = instanceIdRegex.Match(rootTraceRequestUrl);
            if (match.Success)
            {
                instanceOwnerPartyId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                instanceId = Guid.Parse(match.Groups[2].Value);
            }

            var timeGenerated = ReadInstant(row, timeGeneratedIdx);
            var traceId = ReadString(row, operationIdIdx);
            var spanId = ReadString(row, idIdx);

            telemetry.Add(
                new TelemetryEntity
                {
                    Id = 0,
                    ExtId = $"{traceId}-{spanId}",
                    ServiceOwner = serviceOwner.Value,
                    AppName = ReadString(row, appRoleNameIdx),
                    AppVersion = ReadString(row, appVersionIdx),
                    TimeGenerated = timeGenerated,
                    TimeIngested = Instant.MinValue,
                    DupeCount = 0,
                    Seeded = false,
                    Data = new TraceData
                    {
                        AltinnErrorId = i,
                        InstanceOwnerPartyId = instanceOwnerPartyId,
                        InstanceId = instanceId,
                        TraceId = traceId,
                        SpanId = spanId,
                        ParentSpanId = ReadString(row, parentIdIdx),
                        TraceName = ReadString(row, operationNameIdx),
                        SpanName = name,
                        Success = ReadBool(row, successIdx),
                        Result = ReadString(row, resultCodeIdx),
                        Duration = Duration.FromMilliseconds(ReadDouble(row, durationIdx)),
                        Attributes = new()
                        {
                            ["Target"] = ReadString(row, targetIdx),
                            ["DependencyType"] = ReadString(row, dependencyTypeIdx),
                            ["Data"] = ReadString(row, dataIdx),
                            ["PerformanceBucket"] = ReadString(row, performanceBucketIdx),
                            ["Properties"] = ReadString(row, propertiesIdx),
                            ["RootTraceRequestUrl"] = rootTraceRequestUrl,
                        },
                    },
                }
            );
        }

        return telemetry;
    }

    static List<TelemetryEntity> ReadMetrics(ServiceOwner serviceOwner, Query query, int i, LogsTable table)
    {
        var telemetry = new List<TelemetryEntity>(table.Rows.Count);

        var indexes = table.Columns.Index();
        int timeGeneratedIdx = -1;
        int appIdx = -1;
        int appVersionIdx = -1;
        int nameIdx = -1;
        int valueIdx = -1;

        foreach (var (rowIndex, column) in indexes)
        {
            if (column.Name == "TimeGenerated")
                timeGeneratedIdx = rowIndex;
            else if (column.Name == "App")
                appIdx = rowIndex;
            else if (column.Name == "AppVersion")
                appVersionIdx = rowIndex;
            else if (column.Name == "Name")
                nameIdx = rowIndex;
            else if (column.Name == "Value")
                valueIdx = rowIndex;
        }

        for (int j = 0; j < table.Rows.Count; j++)
        {
            var row = table.Rows[j];

            var timeGenerated = ReadInstant(row, timeGeneratedIdx);
            var app = ReadString(row, appIdx);
            var appVersion = ReadString(row, appVersionIdx);
            var name = ReadString(row, nameIdx);
            var value = ReadDouble(row, valueIdx);

            telemetry.Add(
                new TelemetryEntity
                {
                    Id = 0,
                    // NOTE: granularity must not change for a given query
                    ExtId = $"{app}-{appVersion}-{timeGenerated}-{name}-{query.Hash}",
                    ServiceOwner = serviceOwner.Value,
                    AppName = app,
                    AppVersion = appVersion,
                    TimeGenerated = timeGenerated,
                    TimeIngested = Instant.MinValue,
                    DupeCount = 0,
                    Seeded = false,
                    Data = new MetricData
                    {
                        AltinnErrorId = -1,
                        Name = name,
                        Value = value,
                        Attributes = null,
                    },
                }
            );
        }

        return telemetry;
    }

    static string ReadString(LogsTableRow row, int idx, [CallerArgumentExpression("idx")] string? idxName = null)
    {
        if (idx == -1)
            throw new Exception($"Column {idxName} not found in table");

        return row.GetString(idx)
            ?? throw new Exception($"Unexpected value for {idxName} ({idx}): {row[idx].GetType().Name}");
    }

    static bool ReadBool(LogsTableRow row, int idx, [CallerArgumentExpression("idx")] string? idxName = null)
    {
        if (idx == -1)
            throw new Exception($"Column {idxName} not found in table");

        return row.GetBoolean(idx)
            ?? throw new Exception($"Unexpected value for {idxName} ({idx}): {row[idx].GetType().Name}");
    }

    static double ReadDouble(LogsTableRow row, int idx, [CallerArgumentExpression("idx")] string? idxName = null)
    {
        if (idx == -1)
            throw new Exception($"Column {idxName} not found in table");

        return row.GetDouble(idx)
            ?? throw new Exception($"Unexpected value for {idxName} ({idx}): {row[idx].GetType().Name}");
    }

    static Instant ReadInstant(LogsTableRow row, int idx, [CallerArgumentExpression("idx")] string? idxName = null)
    {
        if (idx == -1)
            throw new Exception($"Column {idxName} not found in table");

        return row.GetDateTimeOffset(idx)?.ToInstant()
            ?? throw new Exception($"Unexpected value for {idxName} ({idx}): {row[idx].GetType().Name}");
    }

    [GeneratedRegex(@"(\d+)\/([0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12})")]
    private static partial Regex InstanceIdInUrlRegex();
}
