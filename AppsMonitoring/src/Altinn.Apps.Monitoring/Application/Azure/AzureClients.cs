using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;

namespace Altinn.Apps.Monitoring.Application.Azure;

internal sealed record AzureClients
{
    public ArmClient ArmClient { get; }

    public LogsQueryClient LogsQueryClient { get; }

    public static WorkloadIdentityCredential CreateCredential(IHostEnvironment env)
    {
        // return new AzureCliCredential();
        return new WorkloadIdentityCredential();
    }

    public AzureClients(IHostEnvironment env)
    {
        ArmClient = new(CreateCredential(env));
        LogsQueryClient = new(CreateCredential(env));
    }
}
