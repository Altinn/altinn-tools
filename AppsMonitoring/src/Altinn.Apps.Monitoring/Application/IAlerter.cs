using System.Threading.Channels;
using Altinn.Apps.Monitoring.Application.Db;

namespace Altinn.Apps.Monitoring.Application;

internal sealed record AlerterEvent
{
    public required TelemetryEntity Item { get; init; }
    public required AlertEntity AlertBefore { get; init; }
    public required AlertEntity? AlertAfter { get; init; }
}

internal interface IAlerter : IApplicationService
{
    ChannelReader<AlerterEvent> Events { get; }
}
