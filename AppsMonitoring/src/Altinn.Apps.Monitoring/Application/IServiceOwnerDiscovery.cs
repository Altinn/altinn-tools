using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Application;

public interface IServiceOwnerDiscovery
{
    ValueTask<IReadOnlyList<ServiceOwner>> Discover(CancellationToken cancellationToken);
}
