using System.Text.Json;
using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Application.Slack;
using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Tests.Application.Slack;

public class SlackAlerterTests
{
    [Theory]
    [InlineData(AppFixture.Slack.Cases.Ok)]
    [InlineData(AppFixture.Slack.Cases.Error)]
    [InlineData(AppFixture.Slack.Cases.ServerError)]
    [InlineData(AppFixture.Slack.Cases.RateLimited)]
    [InlineData(AppFixture.Slack.Cases.ServerErrorThenOk)]
    [InlineData(AppFixture.Slack.Cases.RateLimitedThenOk)]
    [InlineData(AppFixture.Slack.Cases.ServerError4TimesThenOk)]
    public async Task Alerts_Eventually_Successfully_For_Non_Seeded_Telemetry(string @case)
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse("skd")];
        await using var fixture = await AppFixture.Create(@case, serviceOwners);
        var orchestratorFixture = fixture.OrchestratorFixture;
        var (hostFixture, startSignal, adapterSemaphore, pollInterval, latency, queries, cancellationToken) =
            orchestratorFixture;

        var timeProvider = hostFixture.TimeProvider;
        var repository = hostFixture.Repository;

        List<OrchestratorEvent> orchestratorEvents = new();
        List<AlerterEvent> alerterEvents = new();

        // Let orchestrator start work
        _ = fixture.StartOrchestrator();

        await fixture.WaitForOrchestratorIteration(orchestratorEvents, cancellationToken);
        await fixture.WaitForAlerterIteration(alerterEvents, cancellationToken);

        var expectedRetries = AppFixture.Slack.ExpectedRetries[@case];

        await Verify(NewSnapshot(orchestratorEvents, alerterEvents, repository, cancellationToken))
            .ScrubMember<TelemetryEntity>(e => e.Data)
            .DontScrubDateTimes()
            .ScrubMember<AlertEntity>(e => e.CreatedAt)
            .ScrubMember<AlertEntity>(e => e.UpdatedAt)
            .DontIgnoreEmptyCollections()
            .UseTextForParameters($"case={@case}_waitForRetryEvents={expectedRetries}");
    }

    private sealed record State(IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<AlertEntity> Alerts);

    private static async ValueTask<object> NewSnapshot(
        IReadOnlyList<OrchestratorEvent> orchestratorEvents,
        IReadOnlyList<AlerterEvent> alerterEvents,
        Repository repository,
        CancellationToken cancellationToken
    )
    {
        var (telemetry, alerts) = await GetState(repository, cancellationToken);
        return new
        {
            OrchestratorEvents = orchestratorEvents,
            AlerterEvents = alerterEvents,
            State = new State(telemetry, alerts),
        };
    }

    private static async Task<(IReadOnlyList<TelemetryEntity> Telemetry, IReadOnlyList<AlertEntity> Alerts)> GetState(
        Repository repository,
        CancellationToken cancellationToken
    )
    {
        var telemetry = await repository.ListTelemetry(cancellationToken: cancellationToken);
        var alerts = await repository.ListAlerts(cancellationToken: cancellationToken);
        return (telemetry, alerts);
    }

    [Fact]
    public async Task Deserialization_Of_Slack_Ok_Response_Succeeds()
    {
        var json = AppFixture.Slack.OkPayload;

        var response = JsonSerializer.Deserialize<SlackAlerter.SlackResponse>(json);
        await Verify(response);
    }

    [Fact]
    public async Task Deserialization_Of_Slack_Error_Response_Succeeds()
    {
        var json = AppFixture.Slack.ErrorPayload;

        var response = JsonSerializer.Deserialize<SlackAlerter.SlackResponse>(json);
        await Verify(response);
    }
}
