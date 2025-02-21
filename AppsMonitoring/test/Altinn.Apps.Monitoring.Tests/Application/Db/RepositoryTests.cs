using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Tests.Application.Db;

public class RepositoryTests
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

    private static Change NewChange(
        string desc,
        Instant start,
        Instant end,
        (IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries) stateBefore,
        (string ServiceOwner, IReadOnlyList<TelemetryEntity> Telemetry) inputData,
        (IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries) stateAfter,
        int persistedEntities
    )
    {
        // Reset data to make snapshots a little less noisy
        var telemetryBefore = stateBefore.Telemetry.Select(t => t with { Data = null! }).ToArray();
        var telemetryAfter = stateAfter.Telemetry.Select(t => t with { Data = null! }).ToArray();
        var inputTelemetry = inputData.Telemetry.Select(t => t with { Data = null! }).ToArray();
        return new Change(
            desc,
            start,
            end,
            new State(telemetryBefore, stateBefore.Queries),
            new(inputData.ServiceOwner, inputTelemetry),
            new State(telemetryAfter, stateAfter.Queries),
            persistedEntities
        );
    }

    [Fact]
    public async Task Test_Insert_Telemetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var fixture = await HostFixture.Create();

        using var _ = await fixture.Start(cancellationToken);

        var timeProvider = fixture.TimeProvider;
        var repository = fixture.Repository;

        var changes = new List<Change>();

        var query = new Query("query-name", QueryType.Traces, "query-template");
        {
            // First write from clean DB
            var start = timeProvider.GetCurrentInstant();
            TelemetryEntity[] telemetry = [TestData.GenerateTelemetryEntity(timeProvider: timeProvider)];
            var serviceOwner = ServiceOwner.Parse(telemetry[0].ServiceOwner);
            var searchFrom = start.Minus(Duration.FromMinutes(20));
            var searchTo = start.Minus(Duration.FromMinutes(10));

            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);

            var persisted = await repository.InsertTelemetry(
                serviceOwner,
                query,
                searchTo,
                telemetry,
                cancellationToken
            );

            var (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            timeProvider.Advance(Duration.FromMinutes(10).ToTimeSpan());
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "First write from clean DB",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    (serviceOwner.Value, telemetry),
                    (telemetryAfter, queryStateAfter),
                    persisted
                )
            );
        }

        {
            // Second write with existing data (test for idempotency)
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);
            var serviceOwner = ServiceOwner.Parse(telemetryBefore[0].ServiceOwner);
            var searchFrom = start.Minus(Duration.FromMinutes(25));
            var searchTo = start.Minus(Duration.FromMinutes(10));

            var telemetry = telemetryBefore.Select(t => t with { Id = 0, TimeIngested = start }).ToArray();
            var persisted = await repository.InsertTelemetry(
                serviceOwner,
                query,
                searchTo,
                telemetry,
                cancellationToken
            );

            var (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            timeProvider.Advance(Duration.FromMinutes(10).ToTimeSpan());
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Second write with existing data (test for idempotency)",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    (serviceOwner.Value, telemetry),
                    (telemetryAfter, queryStateAfter),
                    persisted
                )
            );
        }

        {
            // Same data, different service owner
            var start = timeProvider.GetCurrentInstant();
            var (telemetryBefore, queryStateBefore) = await GetState(repository, cancellationToken);
            var serviceOwner = ServiceOwner.Parse("sot");
            var searchFrom = start.Minus(Duration.FromMinutes(30));
            var searchTo = start.Minus(Duration.FromMinutes(10));

            var telemetry = telemetryBefore
                .Select(t => t with { Id = 0, TimeIngested = start, ServiceOwner = serviceOwner.Value })
                .ToArray();
            var persisted = await repository.InsertTelemetry(
                serviceOwner,
                query,
                searchTo,
                telemetry,
                cancellationToken
            );

            var (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            timeProvider.Advance(Duration.FromMinutes(10).ToTimeSpan());
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                NewChange(
                    "Same data, different service owner, expecting write",
                    start,
                    end,
                    (telemetryBefore, queryStateBefore),
                    (serviceOwner.Value, telemetry),
                    (telemetryAfter, queryStateAfter),
                    persisted
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
        State StateAfter,
        int PersistedEntities
    );

    private sealed record State(IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries);

    private sealed record Input(string ServiceOwner, IReadOnlyList<TelemetryEntity> Telemetry);
}
