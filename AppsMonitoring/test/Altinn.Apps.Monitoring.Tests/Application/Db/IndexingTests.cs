using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Tests.Application.Db;

public class IndexingTests
{
    [Theory(Skip = "This test is not ready yet, index results not deterministic")]
    [InlineData(AppFixture.Slack.Cases.ServerError4TimesThenOk)]
    public async Task Db_Indexes_Are_Not_Missing(string @case)
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse("skd")];
        await using var fixture = await AppFixture.Create(@case, serviceOwners);
        var orchestratorFixture = fixture.OrchestratorFixture;
        var (hostFixture, startSignal, adapterSemaphore, pollInterval, latency, queries, cancellationToken) =
            orchestratorFixture;

        var timeProvider = hostFixture.TimeProvider;
        var repository = hostFixture.Repository;
        var orchestratorResults = hostFixture.Orchestrator.Events;
        var alerterResults = hostFixture.Alerter.Events;

        List<OrchestratorEvent> orchestratorEvents = new();
        List<AlerterEvent> alerterEvents = new();
        // Let orchestrator start work
        _ = fixture.StartOrchestrator();

        for (int i = 0; i < 1; i++)
        {
            await fixture.WaitForOrchestratorIteration(orchestratorEvents, cancellationToken);

            await fixture.WaitForAlerterIteration(alerterEvents, cancellationToken);
        }

        var indexRecommendations = await repository.ListIndexRecommendations(cancellationToken);

        await Verify(indexRecommendations).DontScrubDateTimes().DontIgnoreEmptyCollections().UseParameters(@case);
    }
}
