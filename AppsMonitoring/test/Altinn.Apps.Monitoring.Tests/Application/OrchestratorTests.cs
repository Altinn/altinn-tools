using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;
using Altinn.Apps.Monitoring.Tests.Application.Db;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NodaTime.Text;

namespace Altinn.Apps.Monitoring.Tests.Application;

public class OrchestratorTests
{
    private static async Task<(
        IReadOnlyList<TelemetryEntity> Telemetry,
        IReadOnlyList<QueryStateEntity> Queries
    )> GetState(Repository repository, CancellationToken cancellationToken)
    {
        var telemetry = await repository.ListTelemetry(cancellationToken: cancellationToken);
        var queries = await repository.ListQueryStates(cancellationToken: cancellationToken);
        return (telemetry, queries);
    }

    internal enum TelemetryGenerator
    {
        Empty = 1,
        Multiple = 2,
        WithSeeder = 3,
    }

    private static readonly Dictionary<
        TelemetryGenerator,
        Func<IServiceProvider, TelemetryEntity[]>
    > _telemetryGenerators = new()
    {
        [TelemetryGenerator.Empty] = _ => Array.Empty<TelemetryEntity>(),
        [TelemetryGenerator.Multiple] = sp =>
        {
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            var options = sp.GetRequiredService<IOptions<AppConfiguration>>().Value;
            var serviceOwners =
                sp.GetRequiredService<IOptions<FakeConfig>>().Value.ServiceOwnersDiscovery?.Invoke(sp) ?? [];
            long id = 0;
            var shouldStartFromExclusive = timeProvider
                .GetCurrentInstant()
                .Minus(Duration.FromDays(options.SearchFromDays));
            var shouldEndAtInclusive = timeProvider.GetCurrentInstant().Minus(Duration.FromMinutes(10));

            TelemetryEntity GenerateTrace(ServiceOwner serviceOwner, Instant timeGenerated) =>
                TestData.GenerateTelemetryEntity(
                    extId: $"{id++}",
                    serviceOwner: serviceOwner.Value,
                    timeGenerated: timeGenerated,
                    timeIngested: Instant.MinValue,
                    dataGenerator: () => TestData.GenerateTelemetryTraceData(),
                    timeProvider: timeProvider
                );

            var secondIterationStartExclusive = shouldEndAtInclusive;
            var secondIterationEndInclusive = secondIterationStartExclusive.Plus(Duration.FromMinutes(10));

            return
            [
                GenerateTrace(serviceOwners[0], shouldStartFromExclusive.Minus(Duration.FromSeconds(1))), // This should be outside range
                // 1. Results for the first iteration of queries for "one"
                GenerateTrace(serviceOwners[0], shouldStartFromExclusive), // This should be just within range
                GenerateTrace(serviceOwners[0], shouldEndAtInclusive), // We should include now - 10 minuts
                // 2. Results for the second iteration of queries for "one"
                GenerateTrace(serviceOwners[0], secondIterationStartExclusive.Plus(Duration.FromSeconds(1))), // This should be outside range for first iteration, but included in the second
                GenerateTrace(serviceOwners[0], secondIterationEndInclusive), // This should be just within range
            ];
        },
        [TelemetryGenerator.WithSeeder] = sp =>
        {
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            var serviceOwners =
                sp.GetRequiredService<IOptions<FakeConfig>>().Value.ServiceOwnersDiscovery?.Invoke(sp) ?? [];
            long id = 1;

            return
            [
                TestData.GenerateMiniDbTrace(
                    serviceOwners[0],
                    ref id,
                    InstantPattern.ExtendedIso.Parse("2025-02-15T14:51:04.906736Z").Value,
                    timeProvider
                ), // Should match a record from the seed DB
                TestData.GenerateMiniDbTrace(
                    serviceOwners[0],
                    ref id,
                    InstantPattern.ExtendedIso.Parse("2025-02-15T14:56:04.906736Z").Value,
                    timeProvider
                ), // Different span ID, should not dedupe
            ];
        },
    };

    [Theory]
    [InlineData("one", TelemetryGenerator.Empty)]
    [InlineData("one", TelemetryGenerator.Multiple)]
    [InlineData("skd", TelemetryGenerator.WithSeeder)]
    internal async Task Orchestration_Progresses_Successfully(string serviceOwner, TelemetryGenerator generator)
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse(serviceOwner)];
        await using var fixture = await OrchestratorFixture.Create(
            (services, _) =>
            {
                if (generator == TelemetryGenerator.WithSeeder)
                {
                    var timeProvider = new FakeTimeProvider(Instant.FromUtc(2025, 2, 20, 12, 0, 0).ToDateTimeOffset());
                    services.AddSingleton<TimeProvider>(timeProvider);
                    services.Configure<AppConfiguration>(config =>
                    {
                        config.DisableSeeder = false;
                    });
                }

                services.Configure<FakeConfig>(config =>
                {
                    config.ServiceOwnersDiscovery = _ => serviceOwners;
                    config.TelemetryGenerator = _telemetryGenerators[generator];
                });
            }
        );
        var (hostFixture, startSignal, adapterSemaphore, pollInterval, latency, queries, cancellationToken) = fixture;

        var timeProvider = hostFixture.TimeProvider;
        var repository = hostFixture.Repository;
        var results = hostFixture.Orchestrator.Events;
        var seeder = hostFixture.Seeder;

        if (generator == TelemetryGenerator.WithSeeder)
            await seeder.Completion;

        var changes = new List<Change>();
        IReadOnlyList<TelemetryEntity> telemetryAfter;
        IReadOnlyList<QueryStateEntity> queryStateAfter;
        {
            // Initial loop iterations (discovery and querying)
            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);
            var start = fixture.Start();

            List<OrchestratorEvent> events = new();
            await fixture.WaitForIteration(events, cancellationToken);

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                new Change(
                    $"Iteration 1 - generator={generator}",
                    start,
                    end,
                    new(telemetryBefore, queryStateBefore),
                    new(events),
                    new(telemetryAfter, queryStateAfter)
                )
            );
        }

        if (generator != TelemetryGenerator.WithSeeder)
        {
            var start = fixture.AdvanceToNextIteration();
            var (telemetryBefore, queryStateBefore) = (telemetryAfter, queryStateAfter);

            List<OrchestratorEvent> events = new();
            await fixture.WaitForIteration(events, cancellationToken);

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                new Change(
                    $"Iteration 2 - generator={generator}",
                    start,
                    end,
                    new(telemetryBefore, queryStateBefore),
                    new(events),
                    new(telemetryAfter, queryStateAfter)
                )
            );
        }

        await Verify(changes)
            .ScrubMember<TelemetryEntity>(e => e.Data)
            .DontScrubDateTimes()
            .DontIgnoreEmptyCollections();
    }

    private sealed record Change(
        string Desc,
        Instant Start,
        Instant End,
        State StateBefore,
        Input Input,
        State StateAfter
    );

    private sealed record State(IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries);

    private sealed record Input(IReadOnlyList<OrchestratorEvent> ReportedEvents);
}
