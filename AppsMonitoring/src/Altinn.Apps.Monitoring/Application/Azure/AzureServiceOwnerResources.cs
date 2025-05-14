using System.ComponentModel;
using Altinn.Apps.Monitoring.Domain;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed class AzureServiceOwnerResources(
    IOptionsMonitor<AppConfiguration> config,
    AzureClients clients,
    HybridCache cache,
    Telemetry telemetry
)
{
    private readonly IOptionsMonitor<AppConfiguration> _config = config;
    private readonly ArmClient _armClient = clients.ArmClient;
    private readonly HybridCache _cache = cache;
    private readonly Telemetry _telemetry = telemetry;

    private readonly HybridCacheEntryOptions _cacheEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(30),
        LocalCacheExpiration = TimeSpan.FromMinutes(30),
    };

    public ValueTask<AzureServiceOwnerResourcesRecord?> GetResources(
        ServiceOwner serviceOwner,
        CancellationToken cancellationToken
    )
    {
        using var activity = _telemetry.Activities.StartActivity("AzureServiceOwnerResources.GetResources");
        activity?.SetTag("serviceowner", serviceOwner.Value);
        return _cache.GetOrCreateAsync(
            $"{nameof(AzureServiceOwnerResources)}-{serviceOwner.Value}",
            (this, serviceOwner),
            static async ValueTask<AzureServiceOwnerResourcesRecord?> (state, cancellationToken) =>
            {
                var (self, serviceOwner) = state;
                var config = self._config.CurrentValue;

                if (string.IsNullOrWhiteSpace(serviceOwner.ExtId))
                    return null;

                var env = config.AltinnEnvironment;
                var subscription = await self
                    ._armClient.GetSubscriptions()
                    .GetAsync(serviceOwner.ExtId, cancellationToken: cancellationToken);
                if (subscription is null)
                    return null;

                var rgs = await subscription
                    .Value.GetResourceGroups()
                    .GetAllAsync(cancellationToken: cancellationToken)
                    .ToArrayAsync(cancellationToken);
                var rg = rgs.SingleOrDefault(rg => rg.Data.Name == $"monitor-{serviceOwner.Value}-{env}-rg");
                if (rg is null)
                    return null;

                var workspaces = await rg.GetOperationalInsightsWorkspaces()
                    .GetAllAsync(cancellationToken: cancellationToken)
                    .ToArrayAsync(cancellationToken);
                var workspace = workspaces.SingleOrDefault(ws =>
                    ws.Data.Name == $"application-{serviceOwner.Value}-{env}-law"
                );
                if (workspace is null)
                    return null;

                return new(subscription.Value, rg, workspace);
            },
            options: _cacheEntryOptions,
            cancellationToken: cancellationToken
        );
    }
}

[ImmutableObject(true)]
internal sealed record AzureServiceOwnerResourcesRecord(
    SubscriptionResource Subscription,
    ResourceGroupResource ResourceGroup,
    OperationalInsightsWorkspaceResource Workspace
);
