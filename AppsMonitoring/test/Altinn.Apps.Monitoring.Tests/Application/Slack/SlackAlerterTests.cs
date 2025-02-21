using System.Text.Json;
using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Application.Slack;
using Altinn.Apps.Monitoring.Domain;
using Altinn.Apps.Monitoring.Tests.Application.Db;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NodaTime.Text;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Altinn.Apps.Monitoring.Tests.Application.Slack;

public class SlackAlerterTests
{
    private static readonly string _okPayload = """
        {
            "ok": true,
            "channel": "C01UJ9G",
            "ts": "1634160000.000100"
        }
        """;

    private static readonly string _errorPayload = """
        {
            "ok": false,
            "error": "Something went wrong"
        }
        """;

    [Fact]
    public async Task Test_Alerting()
    {
        ServiceOwner[] serviceOwners = [ServiceOwner.Parse("skd")];
        var (fixture, startSignal, pollInterval, latency, queries, cancellationToken) =
            await OrchestratorTests.CreateFixture(
                (services, fixture) =>
                {
                    fixture
                        .MockServer.Given(
                            Request
                                .Create()
                                .WithPath("/api/chat.postMessage")
                                .WithHeader("Authorization", "Bearer Secret!")
                                .UsingPost()
                        )
                        .RespondWith(Response.Create().WithStatusCode(200).WithBody(_okPayload));

                    var timeProvider = new FakeTimeProvider(Instant.FromUtc(2025, 2, 20, 12, 0, 0).ToDateTimeOffset());
                    services.AddSingleton<TimeProvider>(timeProvider);
                    services.Configure<AppConfiguration>(config =>
                    {
                        config.DisableAlerter = false;
                        config.SlackAccessToken = "Secret!";
                        config.SlackChannel = "C01UJ9G";
                        config.SlackHost =
                            fixture.MockServer.Url ?? throw new InvalidOperationException("Mock server URL is null");

                        config.DisableSeeder = false;
                        config.SeedSqliteDbPath = Path.Combine("data", "mini.db");
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
        var timeProvider = fixture.TimeProvider;
        var repository = fixture.Repository;
        var orchestratorResults = fixture.Orchestrator.Results;
        var alerterResults = fixture.Alerter.Results;

        var total = queries.Count * serviceOwners.Length;
        var start = timeProvider.GetCurrentInstant();
        List<ServiceOwnerQueryResult> queryResults = new();
        List<AlerterEvent> alerterEvents = new();
        {
            // Let orchestrator insert some stuff
            startSignal.SetResult();

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken); // Let all threads reach query delay
            timeProvider.Advance(latency * queries.Count); // Let adapters progress

            for (int i = 0; i < total; i++)
            {
                var result = await orchestratorResults.ReadAsync(cancellationToken);
                queryResults.Add(result);
            }
        }
        {
            var now = timeProvider.GetCurrentInstant();
            // Let alerter pick up the telemetry
            var alerterPollInterval = Duration.FromTimeSpan(pollInterval) / 2;
            var remainingUntilNextAlerterTick = alerterPollInterval - (now - start);
            timeProvider.Advance(remainingUntilNextAlerterTick.ToTimeSpan());

            // We can just fetch all telemetry, since we haven't alerted anything yet,
            // only the seeded ones need to be filtered out
            var telemetry = await repository.ListTelemetry(cancellationToken: cancellationToken);

            for (int i = 0; i < telemetry.Count; i++)
            {
                var item = telemetry[i];
                if (item.Seeded)
                    continue; // Seeded telemetry has already been alerted in old tool

                // Since we don't have mitigation yet, we only expect 1 event
                // per telemetry item
                var result = await alerterResults.ReadAsync(cancellationToken);
                alerterEvents.Add(result);
            }
        }

        await Verify(NewSnapshot(queryResults, alerterEvents, repository, cancellationToken))
            .AutoVerify()
            .ScrubMember<TelemetryEntity>(e => e.Data)
            .DontScrubDateTimes()
            .DontIgnoreEmptyCollections();
    }

    private static async ValueTask<object> NewSnapshot(
        IReadOnlyList<ServiceOwnerQueryResult> queryResults,
        IReadOnlyList<AlerterEvent> alerterEvents,
        Repository repository,
        CancellationToken cancellationToken
    )
    {
        var (telemetry, alerts) = await GetState(repository, cancellationToken);
        return new
        {
            QueryResults = queryResults,
            AlerterEvents = alerterEvents,
            State = (telemetry, alerts),
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
    public async Task Test_Deserialization_Of_Ok_Response()
    {
        var json = _okPayload;

        var response = JsonSerializer.Deserialize<SlackAlerter.SlackResponse>(json);
        await Verify(response).AutoVerify();
    }

    [Fact]
    public async Task Test_Deserialization_Of_Error_Response()
    {
        var json = _errorPayload;

        var response = JsonSerializer.Deserialize<SlackAlerter.SlackResponse>(json);
        await Verify(response).AutoVerify();
    }
}
