using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed record AzureClients
{
    public ArmClient ArmClient { get; }

    public LogsQueryClient LogsQueryClient { get; }

    public AzureClients(IHostEnvironment env)
    {
        TokenCredential credential = env.IsDevelopment() ? new AzureCliCredential() : new ManagedIdentityCredential();

        ArmClient = new(credential);
        LogsQueryClient = new(credential);
    }
}
