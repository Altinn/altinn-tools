using System.Threading.Channels;
using Altinn.Apps.Monitoring.Application.Db;

namespace Altinn.Apps.Monitoring.Application;

public sealed record AlerterEvent
{
    public required TelemetryEntity Item { get; init; }
    public required AlertEntity AlertBefore { get; init; }
    public required AlertEntity AlertAfter { get; init; }
}

public interface IAlerter : IHostedService
{
    ChannelReader<AlerterEvent> Results { get; }
}
