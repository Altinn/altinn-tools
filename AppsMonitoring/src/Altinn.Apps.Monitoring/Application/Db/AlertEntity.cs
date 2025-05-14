using System.Text.Json;
using System.Text.Json.Serialization;
using Altinn.Apps.Monitoring.Application.Slack;

namespace Altinn.Apps.Monitoring.Application.Db;

internal enum AlertState
{
    Pending = 1,
    Alerted = 2,
    Mitigated = 3,
}

internal sealed record AlertEntity
{
    public required long Id { get; init; }

    public required AlertState State { get; init; }

    public required long TelemetryId { get; init; }

    public required AlertData Data { get; init; }

    public required Instant CreatedAt { get; init; }

    public required Instant UpdatedAt { get; init; }
}

[JsonDerivedType(typeof(SlackAlerter.SlackAlertData), typeDiscriminator: Types.Slack)]
internal abstract record AlertData
{
    internal static class Types
    {
        public const string Slack = "slack";

        public static readonly string[] All = typeof(Types)
            .GetFields()
            .Where(f => f.FieldType == typeof(string) && f.IsLiteral)
            .Select(f => f.GetRawConstantValue() as string ?? throw new Exception("Unexpected value"))
            .ToArray();
    }

    public static bool TypeIsValid(string type) => Types.All.Contains(type);

    public bool IsType(string type)
    {
        return this switch
        {
            SlackAlerter.SlackAlertData => type == Types.Slack,
            _ => false,
        };
    }

    public static AlertData Deserialize(string json)
    {
        return JsonSerializer.Deserialize<AlertData>(json, Config.JsonOptions) ?? throw new JsonException();
    }

    public static AlertData Deserialize(byte[] json)
    {
        return JsonSerializer.Deserialize<AlertData>(json, Config.JsonOptions) ?? throw new JsonException();
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
