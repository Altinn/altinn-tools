using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application;

internal sealed class StaticQueryLoader(ILogger<StaticQueryLoader> logger, IOptionsMonitor<AppConfiguration> config)
    : IQueryLoader
{
    private readonly ILogger<StaticQueryLoader> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _config = config;

    public ValueTask<IReadOnlyList<Query>> Load(CancellationToken cancellationToken)
    {
        var config = _config.CurrentValue;
        var target = config.AltinnEnvironment switch
        {
            "prod" => "platform.altinn.no",
            _ => $"platform.{config.AltinnEnvironment}.altinn.no",
        };
        _logger.LogInformation("Loading queries for target: {Target}", target);

        Query[] queries =
        [
            new(
                "Failed Storage instance events",
                QueryType.Traces,
                // * 'OperationName' is present for classic Azure App Insights SDK (the same name as the root span name)
                //   but for OpenTelemetry the root span name is not present on children spans, so we need to use 'OperationName1' (from the join)
                // * 'Target' sometimes has garbage at the end, so we use 'startswith'
                // * This error condition should have failed the root process/next span, so we check 'Success1'
                $$"""
                    AppDependencies
                    | where TimeGenerated > todatetime('{0}') and TimeGenerated <= todatetime('{1}')
                    | where Success == false
                    | where Target startswith "{{target}}"
                    | where Name startswith "POST /storage/api/v1/instances/" and Name endswith "/events"
                    | join kind=inner AppRequests on OperationId
                    | where OperationName1 startswith "PUT Process/NextElement" or OperationName1 endswith "/process/next"
                    | where Success1 == false;
                """
            ),
            new(
                "Failed Altinn events",
                QueryType.Traces,
                // * 'OperationName' is present for classic Azure App Insights SDK (the same name as the root span name)
                //   but for OpenTelemetry the root span name is not present on children spans, so we need to use 'OperationName1' (from the join)
                // * 'Target' sometimes has garbage at the end, so we use 'startswith'
                // * Errors in app.process.completed event does not fail the root process/next span, so we don't check 'Success1' here
                $$"""
                    AppDependencies
                    | where TimeGenerated > todatetime('{0}') and TimeGenerated <= todatetime('{1}')
                    | where Success == false
                    | where Target startswith "{{target}}"
                    | where Name == "POST /events/api/v1/app"
                    | join kind=inner AppRequests on OperationId
                    | where OperationName1 startswith "PUT Process/NextElement" or OperationName1 endswith "/process/next";
                """
            ),
        ];

        return new(queries);
    }
}
