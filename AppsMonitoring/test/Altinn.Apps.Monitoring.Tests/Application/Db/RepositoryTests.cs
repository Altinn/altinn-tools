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

    [Fact]
    public async Task Insert_Telemetry_Is_Idempotent()
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

            var result = await repository.InsertTelemetry(serviceOwner, query, searchTo, telemetry, cancellationToken);

            var (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            timeProvider.Advance(Duration.FromMinutes(10).ToTimeSpan());
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                new Change(
                    "First write from clean DB",
                    start,
                    end,
                    new(telemetryBefore, queryStateBefore),
                    new(serviceOwner.Value, telemetry),
                    new(telemetryAfter, queryStateAfter),
                    result.Written
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
            var result = await repository.InsertTelemetry(serviceOwner, query, searchTo, telemetry, cancellationToken);

            var (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            timeProvider.Advance(Duration.FromMinutes(10).ToTimeSpan());
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                new Change(
                    "Second write with existing data (test for idempotency)",
                    start,
                    end,
                    new(telemetryBefore, queryStateBefore),
                    new(serviceOwner.Value, telemetry),
                    new(telemetryAfter, queryStateAfter),
                    result.Written
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
            var result = await repository.InsertTelemetry(serviceOwner, query, searchTo, telemetry, cancellationToken);

            var (telemetryAfter, queryStateAfter) = await GetState(repository, cancellationToken);
            timeProvider.Advance(Duration.FromMinutes(10).ToTimeSpan());
            var end = timeProvider.GetCurrentInstant();
            changes.Add(
                new Change(
                    "Same data, different service owner, expecting write",
                    start,
                    end,
                    new(telemetryBefore, queryStateBefore),
                    new(serviceOwner.Value, telemetry),
                    new(telemetryAfter, queryStateAfter),
                    result.Written
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
        State StateAfter,
        int PersistedEntities
    );

    private sealed record State(IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<QueryStateEntity> Queries);

    private sealed record Input(string ServiceOwner, IReadOnlyList<TelemetryEntity> Telemetry);
}
