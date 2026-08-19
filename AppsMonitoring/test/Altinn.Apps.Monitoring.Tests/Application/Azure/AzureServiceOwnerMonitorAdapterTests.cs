using System.Text.RegularExpressions;
using Altinn.Apps.Monitoring.Application.Azure;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;
using Azure.Monitor.Query.Models;

namespace Altinn.Apps.Monitoring.Tests.Application.Azure;

public class AzureServiceOwnerMonitorAdapterTests
{
    [Fact]
    public void ReadTraces_UsesJoinedRequestName_WhenDependencyOperationNameIsMissing()
    {
        const string requestName =
            "PUT {org}/{app}/instances/{instanceOwnerPartyId:int}/{instanceGuid:guid}/process/next";
        LogsTableColumn[] columns =
        [
            Column("TimeGenerated", LogsColumnType.Datetime),
            Column("OperationId", LogsColumnType.String),
            Column("Id", LogsColumnType.String),
            Column("ParentId", LogsColumnType.String),
            Column("Name", LogsColumnType.String),
            Column("OperationName", LogsColumnType.String),
            Column("Name1", LogsColumnType.String),
            Column("Url", LogsColumnType.String),
            Column("Target", LogsColumnType.String),
            Column("DependencyType", LogsColumnType.String),
            Column("Data", LogsColumnType.String),
            Column("Success", LogsColumnType.Bool),
            Column("ResultCode", LogsColumnType.String),
            Column("DurationMs", LogsColumnType.Real),
            Column("AppRoleName", LogsColumnType.String),
            Column("AppVersion", LogsColumnType.String),
            Column("PerformanceBucket", LogsColumnType.String),
            Column("Properties", LogsColumnType.String),
        ];
        object[] values =
        [
            new DateTimeOffset(2026, 8, 16, 17, 45, 35, TimeSpan.Zero),
            "ffbc1685e4d9e5bcfcb7c3965ba1b6dd",
            "55d4804c5c51ef4e",
            "e495ceada8e5d80d",
            "POST /events/api/v1/app",
            null!,
            requestName,
            "http://dibk.apps.altinn.no/dibk/nabovarsel-v5/instances/54827750/20ebcaa2-c22c-4122-9303-c14c2f8c221b/process/next",
            "platform.altinn.no",
            "HTTP",
            "https://platform.altinn.no/events/api/v1/app",
            false,
            "0",
            26177.3897,
            "nabovarsel-v5",
            "v1.2.15",
            "15sec-30sec",
            "{}",
        ];
        var row = MonitorQueryModelFactory.LogsTableRow(columns, values);
        var table = MonitorQueryModelFactory.LogsTable("PrimaryResult", columns, [row]);
        var instanceIdRegex = new Regex(
            @"(\d+)/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.CultureInvariant
        );

        var result = AzureServiceOwnerMonitorAdapter.ReadTraces(ServiceOwner.Parse("dibk"), 0, table, instanceIdRegex);

        var telemetry = Assert.Single(result);
        var trace = Assert.IsType<TraceData>(telemetry.Data);
        Assert.Equal(requestName, trace.TraceName);
        Assert.Equal("POST /events/api/v1/app", trace.SpanName);
        Assert.Equal(54827750, trace.InstanceOwnerPartyId);
        Assert.Equal(Guid.Parse("20ebcaa2-c22c-4122-9303-c14c2f8c221b"), trace.InstanceId);
    }

    private static LogsTableColumn Column(string name, LogsColumnType type) =>
        MonitorQueryModelFactory.LogsTableColumn(name, type);
}
