namespace Altinn.Analysis;

public static class Constants
{
    public const string MainBranchFolder = "main";

    // Following settings are to tweak during test-runs
    public static readonly int LimitOrgs = -1;
    public static readonly int LimitRepos = -1;
    public static readonly int LimitMaxParallelism = 8;
}
