using System.ComponentModel;
using Altinn.Apps.Monitoring.Domain;
using Azure;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager.OperationalInsights;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed class AzureServiceOwnerResources(
    ILogger<AzureServiceOwnerResources> logger,
    IOptionsMonitor<AppConfiguration> config,
    AzureClients clients,
    HybridCache cache,
    Telemetry telemetry
)
{
    private readonly ILogger<AzureServiceOwnerResources> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _config = config;
    private readonly AzureClients _clients = clients;
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

                ResourceIdentifier workspaceId;
                try
                {
                    workspaceId = OperationalInsightsWorkspaceResource.CreateResourceIdentifier(
                        serviceOwner.ExtId,
                        $"monitor-{serviceOwner.Value}-{env}-rg",
                        $"application-{serviceOwner.Value}-{env}-law"
                    );
                }
                catch (Exception ex)
                {
                    self._logger.LogWarning(
                        ex,
                        "Failed to create workspace ID for service owner {ServiceOwner}. Subscription ID: '{SubscriptionId}'",
                        serviceOwner.Value,
                        serviceOwner.ExtId
                    );
                    return null;
                }

                try
                {
                    Response<LogsQueryResult> results = await self._clients.LogsQueryClient.QueryResourceAsync(
                        workspaceId,
                        "AppDependencies | project TimeGenerated",
                        new QueryTimeRange(TimeSpan.FromMinutes(5)),
                        cancellationToken: cancellationToken
                    );
                    if (results.Value.Status != LogsQueryResultStatus.Success)
                    {
                        self._logger.LogWarning(
                            "Failed to probe workspace '{WorkspaceId}' for service owner {ServiceOwner}: {Status}/{Error}",
                            workspaceId,
                            serviceOwner.Value,
                            results.Value.Status,
                            results.Value.Error
                        );
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    self._logger.LogWarning(
                        ex,
                        "Failed to probe workspace '{WorkspaceId}' for service owner {ServiceOwner}",
                        workspaceId,
                        serviceOwner.Value
                    );
                    return null;
                }

                return new(workspaceId);
            },
            options: _cacheEntryOptions,
            cancellationToken: cancellationToken
        );
    }
}

[ImmutableObject(true)]
internal sealed record AzureServiceOwnerResourcesRecord(ResourceIdentifier WorkspaceId);
