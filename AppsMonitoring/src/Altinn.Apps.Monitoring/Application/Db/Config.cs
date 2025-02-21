using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime.Serialization.SystemTextJson;

namespace Altinn.Apps.Monitoring.Application.Db;

internal static class Config
{
    internal static JsonSerializerOptions JsonOptions { get; }

    static Config()
    {
        JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            AllowOutOfOrderMetadataProperties = true,
            Converters = { new JsonStringEnumConverter() },
        };
        JsonOptions = JsonOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    }
}
