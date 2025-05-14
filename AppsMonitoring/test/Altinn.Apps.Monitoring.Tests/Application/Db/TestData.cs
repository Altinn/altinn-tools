using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;
using NodaTime.Text;

namespace Altinn.Apps.Monitoring.Tests.Application.Db;

internal static class TestData
{
    public static TraceData GenerateTelemetryTraceData(
        int? altinnErrorId = null,
        int? instanceOwnerPartyId = null,
        Guid? instanceId = null,
        string? traceId = null,
        string? spanId = null,
        string? parentSpanId = null,
        string? traceName = null,
        string? spanName = null,
        bool? success = null,
        string? result = null,
        Duration? duration = null,
        Dictionary<string, string?>? attributes = null
    )
    {
        return new TraceData
        {
            AltinnErrorId = altinnErrorId ?? 1,
            InstanceOwnerPartyId = instanceOwnerPartyId ?? 2,
            InstanceId = instanceId ?? Guid.Parse("12525089-e367-4c64-bc8a-eb1b901553c9"),
            TraceId = traceId ?? "trace-id",
            SpanId = spanId ?? "span-id",
            ParentSpanId = parentSpanId ?? "parent-span-id",
            TraceName = traceName ?? "trace-name",
            SpanName = spanName ?? "span-name",
            Success = success ?? false,
            Result = result ?? "result",
            Duration = duration ?? Duration.FromMilliseconds(100),
            Attributes = attributes ?? new() { ["key1"] = "value1", ["key2"] = "value2" },
        };
    }

    public static TelemetryEntity GenerateTelemetryEntity(
        string? extId = null,
        string? serviceOwner = null,
        string? appName = null,
        string? appVersion = null,
        Instant? timeGenerated = null,
        Instant? timeIngested = null,
        int? dupeCount = null,
        bool? seeded = null,
        Func<TelemetryData>? dataGenerator = null,
        TimeProvider? timeProvider = null
    )
    {
        timeProvider ??= TimeProvider.System;

        return new TelemetryEntity
        {
            Id = 0,
            ExtId = extId ?? "ext-id",
            ServiceOwner = serviceOwner ?? "so",
            AppName = appName ?? "app-name",
            AppVersion = appVersion ?? "8.0.0",
            TimeGenerated = timeGenerated ?? timeProvider.GetCurrentInstant().Minus(Duration.FromMinutes(15)),
            TimeIngested = timeIngested ?? timeProvider.GetCurrentInstant(),
            DupeCount = dupeCount ?? 0,
            Seeded = seeded ?? false,
            Data = dataGenerator?.Invoke() ?? GenerateTelemetryTraceData(),
        };
    }

    public static TelemetryEntity GenerateMiniDbTrace(
        ServiceOwner serviceOwner,
        ref long id,
        Instant timeGenerated,
        TimeProvider timeProvider
    )
    {
        var spanId = $"90c159bde9b1a6c{id++}";
        return TestData.GenerateTelemetryEntity(
            extId: $"75563ff0b3251e04c70362c5a3495174-{spanId}", // Matches Azure adapter
            serviceOwner: serviceOwner.Value,
            appName: "formueinntekt-skattemelding-v2",
            appVersion: "8.0.8",
            timeGenerated: timeGenerated,
            timeIngested: Instant.MinValue,
            dupeCount: 0,
            seeded: false,
            dataGenerator: () =>
                TestData.GenerateTelemetryTraceData(
                    altinnErrorId: 1,
                    instanceOwnerPartyId: 123,
                    instanceId: Guid.Parse("1d449be1-7114-405c-aeee-1f09799f7b74"),
                    traceId: "75563ff0b3251e04c70362c5a3495174",
                    spanId: spanId,
                    parentSpanId: "7e7143a41c29e532",
                    traceName: "PUT Process/NextElement [app/instanceGuid/instanceOwnerPartyId/org]",
                    spanName: "POST /storage/api/v1/instances/123/1d449be1-7114-405c-aeee-1f09799f7b74/events",
                    success: false,
                    result: "Faulted",
                    duration: DurationPattern.Roundtrip.Parse("0:00:00:27.478494").Value,
                    attributes: new()
                    {
                        ["Data"] =
                            "https://platform.altinn.no/storage/api/v1/instances/123/1d449be1-7114-405c-aeee-1f09799f7b74/events",
                        ["DependencyType"] = "HTTP",
                        ["PerformanceBucket"] = "15sec-30sec",
                        ["Properties"] =
                            """{"AspNetCoreEnvironment":"Production","_MS.ProcessedByMetricExtractors":"(Name:'Dependencies', Ver:'1.1')"}""",
                        ["Target"] = "platform.altinn.no",
                    }
                ),
            timeProvider: timeProvider
        );
    }
}
