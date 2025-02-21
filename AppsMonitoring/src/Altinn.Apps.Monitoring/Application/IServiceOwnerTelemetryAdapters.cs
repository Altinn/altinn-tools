using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Application;

public interface IServiceOwnerLogsAdapter
{
    ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    );
}

public interface IServiceOwnerTraceAdapter
{
    ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    );
}

public interface IServiceOwnerMetricsAdapter
{
    ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    );
}
