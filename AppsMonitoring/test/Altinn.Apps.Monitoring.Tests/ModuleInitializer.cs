using System.Runtime.CompilerServices;

namespace Altinn.Apps.Monitoring.Tests;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        Verifier.UseSourceFileRelativeDirectory("_snapshots");
    }
}
