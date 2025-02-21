namespace Altinn.Apps.Monitoring.Application;

public sealed class AppConfiguration
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int SearchFromDays { get; set; } = 90;

    public string SlackHost { get; set; } = "https://slack.com";
    public string SlackAccessToken { get; set; } = null!;
    public string SlackChannel { get; set; } = null!;

    public string AltinnEnvironment { get; set; } = "at24";

    public string DbConnectionString { get; set; } = null!;
    public string SeedSqliteDbPath { get; set; } = Path.Combine("data", "data.db");

    internal bool DisableOrchestrator { get; set; }
    internal bool DisableSeeder { get; set; }
    internal bool DisableAlerter { get; set; }

    internal TaskCompletionSource? OrchestratorStartSignal { get; set; }
}
