using Altinn.Apps.Monitoring.Application.Db;

namespace Altinn.Apps.Monitoring.Tests.Application.Db;

public class TelemetryEntityTests
{
    [Fact]
    public async Task Test_Trace_Data_Serialization()
    {
        var data = TestData.GenerateTelemetryTraceData();

        var json = data.Serialize();

        await VerifyJson(json).AutoVerify();

        var deserializedData = TelemetryData.Deserialize(json);
        var deserializedTraceData = Assert.IsType<TraceData>(deserializedData);
        Assert.Equivalent(data, deserializedTraceData);
    }

    [Fact]
    public async Task Test_Logs_Data_Serialization()
    {
        var data = new LogsData
        {
            AltinnErrorId = 1,
            TraceId = "trace-id",
            SpanId = "span-id",
            Message = "some message",
            Attributes = new() { { "key1", "value1" }, { "key2", "value2" } },
        };

        var json = data.Serialize();

        await VerifyJson(json).AutoVerify();

        var deserializedData = TelemetryData.Deserialize(json);
        var deserializedLogsData = Assert.IsType<LogsData>(deserializedData);
        Assert.Equivalent(data, deserializedLogsData);
    }

    [Fact]
    public async Task Test_Metric_Data_Serialization()
    {
        var data = new MetricData
        {
            AltinnErrorId = -1,
            Name = "metric-name",
            Value = 42,
            Attributes = new() { { "key1", "value1" }, { "key2", "value2" } },
        };

        var json = data.Serialize();

        await VerifyJson(json).AutoVerify();

        var deserializedData = TelemetryData.Deserialize(json);
        var deserializedLogsData = Assert.IsType<MetricData>(deserializedData);
        Assert.Equivalent(data, deserializedLogsData);
    }
}
