using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;
using Altinn.Apps.Monitoring.Tests.Application.Db;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NodaTime.Text;

namespace Altinn.Apps.Monitoring.Tests.Application;

public sealed record FakeConfig
{
    public TimeSpan Latency { get; set; }
    public Func<IServiceProvider, IReadOnlyList<ServiceOwner>>? ServiceOwnersDiscovery { get; set; }
    public Func<IServiceProvider, TelemetryEntity[]>? TelemetryGenerator { get; set; }
}

public class OrchestratorTests
{
    internal static async Task<(
        HostFixture Fixture,
        TaskCompletionSource StartSignal,
        TimeSpan PollInterval,
        TimeSpan Latency,
        IReadOnlyList<Query> Queries,
        CancellationToken CancellationToken
    )> CreateFixture(Action<IServiceCollection, HostFixture> configureServices)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollInterval = TimeSpan.FromMinutes(10);
        var latency = TimeSpan.FromSeconds(5);
        var fixture = await HostFixture.Create(
            (services, fixture) =>
            {
                services.Configure<FakeConfig>(config => config.Latency = latency);
                services.AddSingleton<IServiceOwnerDiscovery, FakeServiceOwnerDiscovery>();
                services.AddSingleton<IQueryLoader, FakeQueryLoader>();
                services.AddSingleton<FakeTelemetryAdapter>();
                services.AddSingleton<IServiceOwnerLogsAdapter>(sp => sp.GetRequiredService<FakeTelemetryAdapter>());
                services.AddSingleton<IServiceOwnerTraceAdapter>(sp => sp.GetRequiredService<FakeTelemetryAdapter>());
                services.AddSingleton<IServiceOwnerMetricsAdapter>(sp => sp.GetRequiredService<FakeTelemetryAdapter>());

                services.Configure<AppConfiguration>(options =>
                {
                    options.DisableOrchestrator = false;
                    options.OrchestratorStartSignal = startSignal;
                    options.PollInterval = pollInterval;
                });

                configureServices(services, fixture);
            }
        );

        using var _ = await fixture.Start(cancellationToken);

        var queryLoader = fixture.QueryLoader;

        var queries = await queryLoader.Load(cancellationToken);

        return (fixture, startSignal, pollInterval, latency, queries, cancellationToken);
    }

    private static async Task<(
        IReadOnlyList<TelemetryEntity> Telemetry,
        IReadOnlyList<QueryStateEntity> Queries
    )> GetState(Repository repository, CancellationToken cancellationToken)
    {
        var telemetry = await repository.ListTelemetry(cancellationToken: cancellationToken);
        var queries = await repository.ListQueryStates(cancellationToken: cancellationToken);
        return (telemetry, queries);
    }

    private static Change NewChange(
        string desc,
        Instant start,
        Instant end,
        (IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries) stateBefore,
        IReadOnlyList<ServiceOwnerQueryResult> inputTelemetry,
        (IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries) stateAfter
    )
    {
        // Reset data to make snapshots a little less noisy
        var telemetryBefore = stateBefore.Telemetry.Select(t => t with { Data = null! }).ToArray();
        var telemetryAfter = stateAfter.Telemetry.Select(t => t with { Data = null! }).ToArray();
        var input = inputTelemetry
            .Select(r =>
            {
                var telemetry = r.Telemetry.Select(t => t with { Data = null! }).ToArray();
                return r with { Telemetry = telemetry };
            })
            .ToArray();
        return new Change(
            desc,
            start,
            end,
            new State(telemetryBefore, stateBefore.Queries),
            new(input),
            new State(telemetryAfter, stateAfter.Queries)
        );
    }

    [Fact]
    public async Task Test_Query_Progression_No_Items()
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse("one")];
        var (fixture, startSignal, pollInterval, latency, queries, cancellationToken) = await CreateFixture(
            (services, _) =>
            {
                services.Configure<FakeConfig>(config =>
                {
                    config.ServiceOwnersDiscovery = _ => serviceOwners;
                });
            }
        );

        var timeProvider = fixture.TimeProvider;
        var repository = fixture.Repository;
        var results = fixture.Orchestrator.Results;

        var changes = new List<Change>();
        IReadOnlyList<TelemetryEntity> telemetryAfter;
        IReadOnlyList<QueryStateEntity> queryStateAfter;
        var total = queries.Count * serviceOwners.Length;
        {
            // Initial loop iterations (discovery and querying)
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);
            startSignal.SetResult();

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken); // Let all threads reach query delay
            timeProvider.Advance(latency * queries.Count); // Let adapters progress

            List<ServiceOwnerQueryResult> queryResults = new();
            for (int i = 0; i < total; i++)
            {
                var result = await results.ReadAsync(cancellationToken);
                queryResults.Add(result);
            }

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Iteration 1 - no records",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    queryResults,
                    (telemetryAfter, queryStateAfter)
                )
            );
        }

        {
            timeProvider.Advance(pollInterval - (latency * queries.Count));
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = (telemetryAfter, queryStateAfter);

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken); // Let all threads reach query delay
            timeProvider.Advance(latency * queries.Count); // Let adapters progress

            List<ServiceOwnerQueryResult> queryResults = new();
            for (int i = 0; i < total; i++)
            {
                var result = await results.ReadAsync(cancellationToken);
                queryResults.Add(result);
            }

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Iteration 2 - no records",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    queryResults,
                    (telemetryAfter, queryStateAfter)
                )
            );
        }

        await Verify(changes).AutoVerify().DontScrubDateTimes().DontIgnoreEmptyCollections();
    }

    [Fact]
    public async Task Test_Query_Progression_With_Items()
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse("one")];
        var (fixture, startSignal, pollInterval, latency, queries, cancellationToken) = await CreateFixture(
            (services, _) =>
                services.Configure<FakeConfig>(config =>
                {
                    config.ServiceOwnersDiscovery = _ => serviceOwners;
                    config.TelemetryGenerator = sp =>
                    {
                        var timeProvider = sp.GetRequiredService<TimeProvider>();
                        var options = sp.GetRequiredService<IOptions<AppConfiguration>>().Value;
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
                            GenerateTrace(
                                serviceOwners[0],
                                secondIterationStartExclusive.Plus(Duration.FromSeconds(1))
                            ), // This should be outside range for first iteration, but included in the second
                            GenerateTrace(serviceOwners[0], secondIterationEndInclusive), // This should be just within range
                        ];
                    };
                })
        );
        var timeProvider = fixture.TimeProvider;
        var repository = fixture.Repository;
        var results = fixture.Orchestrator.Results;

        var changes = new List<Change>();
        IReadOnlyList<TelemetryEntity> telemetryAfter;
        IReadOnlyList<QueryStateEntity> queryStateAfter;
        var total = queries.Count * serviceOwners.Length;
        {
            // Initial loop iterations (discovery and querying)
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);
            startSignal.SetResult();

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken); // Let all threads reach query delay
            timeProvider.Advance(latency * queries.Count); // Let adapters progress

            List<ServiceOwnerQueryResult> queryResults = new();
            for (int i = 0; i < total; i++)
            {
                var result = await results.ReadAsync(cancellationToken);
                queryResults.Add(result);
            }

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Iteration 1 - 2 records",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    queryResults,
                    (telemetryAfter, queryStateAfter)
                )
            );
        }

        {
            timeProvider.Advance(pollInterval - (latency * queries.Count));
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = (telemetryAfter, queryStateAfter);

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken); // Let all threads reach query delay
            timeProvider.Advance(latency * queries.Count); // Let adapters progress

            List<ServiceOwnerQueryResult> queryResults = new();
            for (int i = 0; i < total; i++)
            {
                var result = await results.ReadAsync(cancellationToken);
                queryResults.Add(result);
            }

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Iteration 2 - 2 records",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    queryResults,
                    (telemetryAfter, queryStateAfter)
                )
            );
        }

        await Verify(changes).AutoVerify().DontScrubDateTimes().DontIgnoreEmptyCollections();
    }

    [Fact]
    public async Task Test_Querying_After_Seeding()
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse("skd")];
        var (fixture, startSignal, pollInterval, latency, queries, cancellationToken) = await CreateFixture(
            (services, _) =>
            {
                var timeProvider = new FakeTimeProvider(Instant.FromUtc(2025, 2, 20, 12, 0, 0).ToDateTimeOffset());
                services.AddSingleton<TimeProvider>(timeProvider);
                services.Configure<AppConfiguration>(config =>
                {
                    config.DisableSeeder = false;
                    config.SeedSqliteDbPath = Path.Combine("data", "mini.db");
                });
                services.Configure<FakeConfig>(config =>
                {
                    config.ServiceOwnersDiscovery = _ => serviceOwners;
                    config.TelemetryGenerator = (sp) =>
                    {
                        var timeProvider = sp.GetRequiredService<TimeProvider>();
                        var options = sp.GetRequiredService<IOptions<AppConfiguration>>().Value;
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
                    };
                });
            }
        );
        var timeProvider = fixture.TimeProvider;
        var repository = fixture.Repository;
        var results = fixture.Orchestrator.Results;
        var seeder = fixture.Seeder;

        await seeder.Completion;

        var changes = new List<Change>();
        IReadOnlyList<TelemetryEntity> telemetryAfter;
        IReadOnlyList<QueryStateEntity> queryStateAfter;
        var total = queries.Count * serviceOwners.Length;
        {
            // Initial loop iterations (discovery and querying)
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);
            startSignal.SetResult();

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken); // Let all threads reach query delay
            timeProvider.Advance(latency * queries.Count); // Let adapters progress

            List<ServiceOwnerQueryResult> queryResults = new();
            for (int i = 0; i < total; i++)
            {
                var result = await results.ReadAsync(cancellationToken);
                queryResults.Add(result);
            }

            (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Iteration 1 - dupe += 1",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    queryResults,
                    (telemetryAfter, queryStateAfter)
                )
            );
        }

        await Verify(changes).AutoVerify().DontScrubDateTimes().DontIgnoreEmptyCollections();
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

    private sealed record Input(IReadOnlyList<ServiceOwnerQueryResult> ReportedResults);

    private sealed class FakeServiceOwnerDiscovery(IOptions<FakeConfig> config, IServiceProvider serviceProvider)
        : IServiceOwnerDiscovery
    {
        private readonly FakeConfig _config = config.Value;
        private readonly IServiceProvider _serviceProvider = serviceProvider;

        public ValueTask<IReadOnlyList<ServiceOwner>> Discover(CancellationToken cancellationToken)
        {
            return new(
                _config.ServiceOwnersDiscovery is not null ? _config.ServiceOwnersDiscovery(_serviceProvider) : []
            );
        }
    }

    private sealed class FakeQueryLoader : IQueryLoader
    {
        public ValueTask<IReadOnlyList<Query>> Load(CancellationToken cancellationToken)
        {
            IReadOnlyList<Query> queries = [new Query("query", QueryType.Traces, "template-{searchFrom}-{searchTo}")];
            return new(queries);
        }
    }

    private sealed class FakeTelemetryAdapter
        : IServiceOwnerLogsAdapter,
            IServiceOwnerTraceAdapter,
            IServiceOwnerMetricsAdapter
    {
        private readonly TimeSpan _latency;
        private readonly TimeProvider _timeProvider;

        public FakeTelemetryAdapter(
            IServiceProvider serviceProvider,
            TimeProvider timeProvider,
            IOptions<FakeConfig> fakeConfig
        )
        {
            _latency = fakeConfig.Value.Latency;
            _timeProvider = timeProvider;

            _telemetry = fakeConfig.Value.TelemetryGenerator is not null
                ? fakeConfig.Value.TelemetryGenerator(serviceProvider)
                : [];
        }

        private readonly TelemetryEntity[] _telemetry;

        public async ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
            ServiceOwner serviceOwner,
            Query query,
            Instant from,
            Instant to,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(_latency, _timeProvider, cancellationToken);
            switch (query.Type)
            {
                case QueryType.Traces:
                {
                    var table = _telemetry
                        .Where(t =>
                            t.ServiceOwner == serviceOwner.Value && t.TimeGenerated > from && t.TimeGenerated <= to
                        )
                        .ToArray();
                    return [table];
                }
                default:
                    throw new Exception("Invalid query type: " + query.Type);
            }
        }
    }
}
