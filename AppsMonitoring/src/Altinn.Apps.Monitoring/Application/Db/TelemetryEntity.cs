using System.Text.Json;
using System.Text.Json.Serialization;

namespace Altinn.Apps.Monitoring.Application.Db;

internal sealed record TelemetryEntity
{
    public required long Id { get; init; }
    public required string ExtId { get; init; }
    public required string ServiceOwner { get; init; }
    public required string AppName { get; init; }
    public required string AppVersion { get; init; }
    public required Instant TimeGenerated { get; init; }
    public required Instant TimeIngested { get; init; }
    public required long DupeCount { get; init; }
    public required bool Seeded { get; init; }
    public required TelemetryData Data { get; init; }
}

[JsonDerivedType(typeof(TraceData), typeDiscriminator: "trace")]
[JsonDerivedType(typeof(LogsData), typeDiscriminator: "logs")]
[JsonDerivedType(typeof(MetricData), typeDiscriminator: "metric")]
internal abstract class TelemetryData
{
    public required int AltinnErrorId { get; init; }

    public static TelemetryData Deserialize(string json)
    {
        return JsonSerializer.Deserialize<TelemetryData>(json, Config.JsonOptions) ?? throw new JsonException();
    }

    public static TelemetryData Deserialize(byte[] json)
    {
        return JsonSerializer.Deserialize<TelemetryData>(json, Config.JsonOptions) ?? throw new JsonException();
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, Config.JsonOptions);
    }

    public byte[] SerializeToUtf8Bytes()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, Config.JsonOptions);
    }
}

internal sealed class TraceData : TelemetryData
{
    public required int? InstanceOwnerPartyId { get; init; }
    public required Guid? InstanceId { get; init; }
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public required string? ParentSpanId { get; init; }
    public required string TraceName { get; init; }
    public required string SpanName { get; init; }
    public required bool? Success { get; init; }
    public required string? Result { get; init; }
    public required Duration Duration { get; init; }
    public required Dictionary<string, string?>? Attributes { get; init; }
}

internal sealed class LogsData : TelemetryData
{
    public required string? TraceId { get; init; }
    public required string? SpanId { get; init; }
    public required string Message { get; init; }
    public required Dictionary<string, string?>? Attributes { get; init; }
}

internal sealed class MetricData : TelemetryData
{
    public required string Name { get; init; }
    public required double Value { get; init; }
    public required Dictionary<string, string?>? Attributes { get; init; }
}
