using System.ComponentModel;
using Altinn.Apps.Monitoring.Domain;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Caching.Hybrid;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed class AzureServiceOwnerResources(AzureClients clients, HybridCache cache)
{
    private readonly ArmClient _armClient = clients.ArmClient;
    private readonly HybridCache _cache = cache;

    private readonly HybridCacheEntryOptions _cacheEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(10),
    };

    public ValueTask<AzureServiceOwnerResourcesRecord?> GetResources(
        ServiceOwner serviceOwner,
        CancellationToken cancellationToken
    )
    {
        return _cache.GetOrCreateAsync(
            $"{nameof(AzureServiceOwnerResources)}-{serviceOwner.Value}",
            (this, serviceOwner),
            static async ValueTask<AzureServiceOwnerResourcesRecord?> (state, cancellationToken) =>
            {
                var (self, serviceOwner) = state;

                var env = "prod"; // TODO: from env?
                var subscription = await self
                    ._armClient.GetSubscriptions()
                    .GetAsync(
                        $"Altinn-{serviceOwner.Value.ToUpperInvariant()}-{$"{char.ToUpperInvariant(env[0])}{env[1..]}"}",
                        cancellationToken: cancellationToken
                    );
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
