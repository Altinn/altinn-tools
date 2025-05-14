using System.Diagnostics;

namespace Altinn.Apps.Monitoring.Application;

internal static class TelemetryExtensions
{
    public static Activity? StartRootActivity(this ActivitySource source, string name)
    {
        var previous = Activity.Current;
        Activity.Current = null;
        var activity = source.StartActivity(name, default, parentContext: default);
        if (previous is not null)
            activity?.AddLink(new(previous.Context));
        if (activity is not null)
            previous?.AddLink(new(activity.Context));
        return activity;
    }
}

internal sealed class Telemetry : IDisposable
{
    public const string ActivitySourceName = "Altinn.Apps.Monitoring";

    public Telemetry()
    {
        Activities = new ActivitySource(ActivitySourceName);
    }

    public ActivitySource Activities { get; }

    public void Dispose()
    {
        Activities.Dispose();
    }
}
