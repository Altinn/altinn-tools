using System.Runtime.CompilerServices;

namespace Altinn.Apps.Monitoring.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifierSettings.AutoVerify(includeBuildServer: false);
        Verifier.UseSourceFileRelativeDirectory("_snapshots");
    }
}
