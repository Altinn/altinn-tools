namespace Altinn.Apps.Monitoring.Application;

internal sealed class AppConfiguration
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int SearchFromDays { get; set; } = 90;

    public string SlackHost { get; set; } = "https://slack.com";
    public string SlackAccessToken { get; set; } = null!;
    public string SlackChannel { get; set; } = null!;

    public string AltinnEnvironment { get; set; } = "at24";

    public string DbConnectionString { get; set; } = null!;
    public string SeedSqliteDbPath { get; set; } = Path.Combine("data", "data.db");

    public string KeyVaultName { get; set; } = null!;

    public bool DisableOrchestrator { get; set; }
    public bool DisableSeeder { get; set; }
    public bool DisableAlerter { get; set; }
    public bool DisableSlackAlerts { get; set; }

    internal TaskCompletionSource? OrchestratorStartSignal { get; set; }
}
