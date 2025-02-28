using System.Collections.Concurrent;
using Altinn.Apps.Monitoring.Domain;
using Azure.ResourceManager;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed class AzureServiceOwnerDiscovery(
    ILogger<AzureServiceOwnerDiscovery> logger,
    IOptionsMonitor<AppConfiguration> config,
    AzureClients clients,
    AzureServiceOwnerResources serviceOwnerResources
) : IServiceOwnerDiscovery
{
    private readonly ILogger<AzureServiceOwnerDiscovery> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _config = config;
    private readonly ArmClient _armClient = clients.ArmClient;
    private readonly AzureServiceOwnerResources _serviceOwnerResources = serviceOwnerResources;

    public async ValueTask<IReadOnlyList<ServiceOwner>> Discover(CancellationToken cancellationToken)
    {
        var env = _config.CurrentValue.AltinnEnvironment;
        var envToMatch = env switch
        {
            "prod" => "prod",
            "at24" => "test",
            "tt02" => "test",
            _ => throw new Exception("Unexpected environment: " + env),
        };
        var serviceOwners = new ConcurrentBag<ServiceOwner>();
        await Parallel.ForEachAsync(
            _armClient.GetSubscriptions().GetAllAsync(cancellationToken),
            async (subscription, cancellationToken) =>
            {
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
        _logger.LogInformation("Discovered {Count} service owners", result.Length);
        return result;
    }
}
