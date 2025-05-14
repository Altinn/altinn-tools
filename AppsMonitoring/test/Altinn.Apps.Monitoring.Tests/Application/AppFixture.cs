using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Domain;
using Altinn.Apps.Monitoring.Tests.Application.Db;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NodaTime.Text;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Altinn.Apps.Monitoring.Tests.Application;

internal sealed record AppFixture(string Case, OrchestratorFixture OrchestratorFixture) : IAsyncDisposable
{
    internal static class Slack
    {
        internal const string OkPayload = """
            {
                "ok": true,
                "channel": "C01UJ9G",
                "ts": "1634160000.000100"
            }
            """;

        internal const string ErrorPayload = """
            {
                "ok": false,
                "error": "Something went wrong"
            }
            """;

        internal static readonly IRequestBuilder SlackChatPostMessageRequest = Request
            .Create()
            .WithPath("/api/chat.postMessage")
            .WithHeader("Authorization", "Bearer Secret!")
            .UsingPost();

        internal static class Cases
        {
            internal const string Ok = "200-ok";
            internal const string Error = "200-error";
            internal const string ServerError = "500-error";
            internal const string RateLimited = "429-ratelimited";
            internal const string ServerErrorThenOk = "500-error-then-ok";
            internal const string RateLimitedThenOk = "429-ratelimited-then-ok";
            internal const string ServerError4TimesThenOk = "500-error-4-times-then-ok";
        }

        internal static readonly Dictionary<string, int> ExpectedRetries = new()
        {
            [Cases.Ok] = 0,
            [Cases.Error] = 0,
            [Cases.ServerError] = 0,
            [Cases.RateLimited] = 0,
            [Cases.ServerErrorThenOk] = 0,
            [Cases.RateLimitedThenOk] = 0,
            [Cases.ServerError4TimesThenOk] = 1,
        };

        // IXunitSerializable makes each test case appear in test explorer
        // Source: https://github.com/xunit/xunit/issues/429#issuecomment-108187109
        // We put this delegate dictionary here to avoid serialization, and only put
        // serializable stuff in the memberdata/test cases below
        internal static readonly Dictionary<string, Action<WireMockServer>> MockServerConfigurations = new()
        {
            [Cases.Ok] = server =>
                server
                    .Given(SlackChatPostMessageRequest)
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody(OkPayload)),
            [Cases.Error] = server =>
                server
                    .Given(SlackChatPostMessageRequest)
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody(ErrorPayload)),
            [Cases.ServerError] = server =>
                server.Given(SlackChatPostMessageRequest).RespondWith(Response.Create().WithStatusCode(500)),
            [Cases.RateLimited] = server =>
                server.Given(SlackChatPostMessageRequest).RespondWith(Response.Create().WithStatusCode(429)),
            [Cases.ServerErrorThenOk] = server =>
            {
                server
                    .Given(SlackChatPostMessageRequest)
                    .InScenario("recovery-from-500")
                    .WillSetStateTo("error")
                    .RespondWith(Response.Create().WithStatusCode(500));
                server
                    .Given(SlackChatPostMessageRequest)
                    .InScenario("recovery-from-500")
                    .WhenStateIs("error")
                    .WillSetStateTo("ok")
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody(OkPayload));
            },
            [Cases.RateLimitedThenOk] = server =>
            {
                server
                    .Given(SlackChatPostMessageRequest)
                    .InScenario("recovery-from-429")
                    .WillSetStateTo("error")
                    .RespondWith(Response.Create().WithStatusCode(429));
                server
                    .Given(SlackChatPostMessageRequest)
                    .InScenario("recovery-from-429")
                    .WhenStateIs("error")
                    .WillSetStateTo("ok")
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody(OkPayload));
            },
            [Cases.ServerError4TimesThenOk] = server =>
            {
                server
                    .Given(SlackChatPostMessageRequest)
                    .InScenario("recovery-from-500-4-times")
                    .WillSetStateTo("error", 4)
                    .RespondWith(Response.Create().WithStatusCode(500));
                server
                    .Given(SlackChatPostMessageRequest)
                    .InScenario("recovery-from-500-4-times")
                    .WhenStateIs("error")
                    .WillSetStateTo("ok")
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody(OkPayload));
            },
        };
    }

    public Instant StartOrchestrator() => OrchestratorFixture.Start();

    public Instant AdvanceToNextOrchestratorIteration() => OrchestratorFixture.AdvanceToNextIteration();

    public List<OrchestratorEvent> OrchestratorEvents { get; private set; } = new();

    public async ValueTask WaitForOrchestratorIteration(
        List<OrchestratorEvent> orchestratorEvents,
        CancellationToken cancellationToken
    )
    {
        await OrchestratorFixture.WaitForIteration(orchestratorEvents, cancellationToken);
        OrchestratorEvents = orchestratorEvents;
    }

    public async ValueTask WaitForAlerterIteration(
        List<AlerterEvent> alerterEvents,
        CancellationToken cancellationToken
    )
    {
        var timeProvider = OrchestratorFixture.HostFixture.TimeProvider;
        var alerterResults = OrchestratorFixture.HostFixture.Alerter.Events;

        var events = OrchestratorEvents;

        for (int i = 0; i < events.Count; i++)
        {
            var @event = events[i];
            for (int k = 0; k < @event.Telemetry.Count; k++)
            {
                var item = @event.Telemetry[k];
                if (item.Seeded || @event.Result.DupeExtIds.Contains(item.ExtId))
                    continue; // Filter out similarly to the alerter worklist

                // Since we don't have mitigation yet, we only expect 1 event
                // per telemetry item
                for (int j = 0; j < Slack.ExpectedRetries[Case] + 1; j++)
                {
                    AlerterEvent? result;
                    while (!alerterResults.TryRead(out result))
                    {
                        // TODO: can't advance time arbitrarily like this if we want to
                        // be able to advance both the orchestrator and the alerter in lockstep
                        // Need some other mechanism.... But this is future work
                        await Task.Delay(10, cancellationToken);
                        timeProvider.Advance(TimeSpan.FromSeconds(2)); // Initial HttpClient retry delay is 2s
                    }
                    alerterEvents.Add(result);
                }
            }
        }
    }

    public static async ValueTask<AppFixture> Create(string @case, ServiceOwner[] serviceOwners)
    {
        var fixture = await OrchestratorFixture.Create(
            (services, fixture) =>
            {
                Slack.MockServerConfigurations[@case](fixture.MockServer);

                var timeProvider = new FakeTimeProvider(Instant.FromUtc(2025, 2, 20, 12, 0, 0).ToDateTimeOffset());
                services.AddSingleton<TimeProvider>(timeProvider);
                services.Configure<AppConfiguration>(config =>
                {
                    config.DisableAlerter = false;
                    config.DisableSlackAlerts = false;
                    config.SlackAccessToken = "Secret!";
                    config.SlackChannel = "C01UJ9G";
                    config.SlackHost =
                        fixture.MockServer.Url ?? throw new InvalidOperationException("Mock server URL is null");

                    config.DisableSeeder = false;
                });

                services.Configure<FakeConfig>(config =>
                {
                    config.ServiceOwnersDiscovery = _ => serviceOwners;
                    config.TelemetryGenerator = sp =>
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
                            ), // Should match a record from the seed DB, expect no alert from this
                            TestData.GenerateMiniDbTrace(
                                serviceOwners[0],
                                ref id,
                                InstantPattern.ExtendedIso.Parse("2025-02-15T14:56:04.906736Z").Value,
                                timeProvider
                            ), // Different span ID, should not dedupe, so we expect alert for this one
                        ];
                    };
                });
            }
        );

        return new AppFixture(@case, fixture);
    }

    public ValueTask DisposeAsync()
    {
        return OrchestratorFixture.DisposeAsync();
    }
}
