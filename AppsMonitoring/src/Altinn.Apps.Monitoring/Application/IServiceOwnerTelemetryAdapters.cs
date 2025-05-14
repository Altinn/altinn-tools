using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;

namespace Altinn.Apps.Monitoring.Application;

internal interface IServiceOwnerLogsAdapter
{
    ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    );
}

internal interface IServiceOwnerTraceAdapter
{
    ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    );
}

internal interface IServiceOwnerMetricsAdapter
{
    ValueTask<IReadOnlyList<IReadOnlyList<TelemetryEntity>>> Query(
        ServiceOwner serviceOwner,
        Query query,
        Instant from,
        Instant to,
        CancellationToken cancellationToken
    );
}
