using System.Collections.Concurrent;
using Altinn.Apps.Monitoring.Domain;
using Azure.ResourceManager;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed class AzureServiceOwnerDiscovery(
    ILogger<AzureServiceOwnerDiscovery> logger,
    IOptionsMonitor<AppConfiguration> config,
    AzureClients clients,
    AzureServiceOwnerResources serviceOwnerResources,
    Telemetry telemetry
) : IServiceOwnerDiscovery
{
    private readonly ILogger<AzureServiceOwnerDiscovery> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _config = config;
    private readonly ArmClient _armClient = clients.ArmClient;
    private readonly AzureServiceOwnerResources _serviceOwnerResources = serviceOwnerResources;
    private readonly Telemetry _telemetry = telemetry;
    private long _iteration = -1;

    public async ValueTask<IReadOnlyList<ServiceOwner>> Discover(CancellationToken cancellationToken)
    {
        using var activity = _telemetry.Activities.StartActivity("AzureServiceOwnerDiscovery.Discover");
        var env = _config.CurrentValue.AltinnEnvironment;
        var envToMatch = env switch
        {
            "prod" => "prod",
            "at24" => "test",
            "tt02" => "test",
            _ => throw new Exception("Unexpected environment: " + env),
        };
        var iteration = Interlocked.Increment(ref _iteration);
        var serviceOwners = new ConcurrentBag<ServiceOwner>();
        await Parallel.ForEachAsync(
            _armClient.GetSubscriptions().GetAllAsync(cancellationToken),
            async (subscription, cancellationToken) =>
            {
                if (iteration == 0)
                {
                    _logger.LogInformation(
                        "Found Subscription {SubscriptionId}: {DisplayName}",
                        subscription.Id.SubscriptionId,
                        subscription.Data.DisplayName
                    );
                }

                if (!subscription.Data.DisplayName.StartsWith("altinn", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!subscription.Data.DisplayName.EndsWith(envToMatch, StringComparison.OrdinalIgnoreCase))
                    return;

                var split = subscription.Data.DisplayName.Split('-');
                if (split.Length != 3)
                    return;

                var serviceOwnerValue = split[1];
                if (serviceOwnerValue.Any(c => char.IsLower(c) || !char.IsLetter(c)))
                    return;

#pragma warning disable CA1308 // Normalize strings to uppercase
                var serviceOwner = ServiceOwner.Parse(
                    serviceOwnerValue.ToLowerInvariant(),
                    subscription.Id.SubscriptionId
                );
#pragma warning restore CA1308 // Normalize strings to uppercase
                var resources = await _serviceOwnerResources.GetResources(serviceOwner, cancellationToken);
                if (resources is null)
                    return;

                serviceOwners.Add(serviceOwner);
            }
        );

        var result = serviceOwners.ToArray();
        activity?.SetTag("serviceowners.count", result.Length);
        _logger.LogInformation("Discovered {Count} service owners", result.Length);
        return result;
    }
}
