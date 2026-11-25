using Altinn.Apps.Monitoring.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Tests.Application.Querying;

public class StaticQueryLoaderTests
{
    [Fact]
    public async Task TraceQueries_FilterOnJoinedRequestSpanName()
    {
        var config = new OptionsMonitor(new AppConfiguration { AltinnEnvironment = "prod" });
        var loader = new StaticQueryLoader(NullLogger<StaticQueryLoader>.Instance, config);

        var queries = await loader.Load(TestContext.Current.CancellationToken);

        var traceQueries = queries.Where(query => query.Type == QueryType.Traces).ToArray();
        Assert.NotEmpty(traceQueries);
        Assert.All(
            traceQueries,
            query =>
            {
                Assert.Contains(
                    "| where Name1 startswith \"PUT Process/NextElement\" or Name1 endswith \"/process/next\"",
                    query.QueryTemplate,
                    StringComparison.Ordinal
                );
                Assert.DoesNotContain("| where OperationName1", query.QueryTemplate, StringComparison.Ordinal);
            }
        );
    }

    private sealed class OptionsMonitor(AppConfiguration currentValue) : IOptionsMonitor<AppConfiguration>
    {
        public AppConfiguration CurrentValue { get; } = currentValue;

        public AppConfiguration Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppConfiguration, string?> listener) => null;
    }
}
