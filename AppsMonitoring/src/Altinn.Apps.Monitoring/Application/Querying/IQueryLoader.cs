namespace Altinn.Apps.Monitoring.Application;

internal interface IQueryLoader
{
    ValueTask<IReadOnlyList<Query>> Load(CancellationToken cancellationToken);
}
