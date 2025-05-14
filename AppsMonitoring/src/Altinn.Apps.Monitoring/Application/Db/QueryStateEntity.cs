namespace Altinn.Apps.Monitoring.Application.Db;

internal sealed record QueryStateEntity
{
    public required long Id { get; init; }
    public required string ServiceOwner { get; init; }
    public required string Name { get; init; }
    public required string Hash { get; init; }
    public required Instant QueriedUntil { get; init; }
}
