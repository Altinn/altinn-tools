using Altinn.Apps.Monitoring.Application;

namespace Altinn.Apps.Monitoring.Tests.Application.Db;

public class SeederTests
{
    [Fact]
    public async Task Seeds_Db_Successfully()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var fixture = await HostFixture.Create(
            (services, _) =>
            {
                services.Configure<AppConfiguration>(config =>
                {
                    config.DisableSeeder = false;
                });
            }
        );

        using var _ = await fixture.Start(cancellationToken);

        var seeder = fixture.Seeder;
        var repository = fixture.Repository;

        await seeder.Completion;

        var telemetry = await repository.ListTelemetry(cancellationToken: cancellationToken);
        var queryStates = await repository.ListQueryStates(cancellationToken: cancellationToken);

        await Verify(new { Telemetry = telemetry, QueryStates = queryStates })
            .DontScrubDateTimes()
            .DontIgnoreEmptyCollections();
    }
}
