using System.Diagnostics;

namespace Altinn.Apps.Monitoring.Application;

internal sealed class Telemetry : IDisposable
{
    public const string ActivitySourceName = "Altinn.Apps.Monitoring";

    public Telemetry()
    {
        Activities = new ActivitySource(ActivitySourceName);
    }

    public ActivitySource Activities { get; }

    public Activity? StartRootActivity(string name)
    {
        // Ref: https://github.com/open-telemetry/opentelemetry-dotnet/blob/807aa26d20a12c16165795555ccf10bbbb1dc8d1/src/OpenTelemetry.Api/Trace/Tracer.cs#L75
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            return Activities.StartActivity(name, default, parentContext: default);
        }
        finally
        {
            if (Activity.Current != previous)
                Activity.Current = previous;
        }
    }

    public void Dispose()
    {
        Activities.Dispose();
    }
}
