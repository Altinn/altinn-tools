using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Application;

internal interface IServiceOwnerDiscovery
{
    ValueTask<IReadOnlyList<ServiceOwner>> Discover(CancellationToken cancellationToken);
}
