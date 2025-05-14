namespace Altinn.Apps.Monitoring.Application;

internal sealed class AppConfiguration
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);
    public int SearchFromDays { get; set; } = 90;

    public string SlackHost { get; set; } = "https://slack.com";
    public string SlackAccessToken { get; set; } = null!;
    public string SlackChannel { get; set; } = null!;

    public string AltinnEnvironment { get; set; } = "at24";

    public DbConfiguration Db { get; set; } = null!;
    public DbConfiguration DbAdmin { get; set; } = null!;

    public string KeyVaultUri { get; set; } = null!;

    public bool DisableOrchestrator { get; set; }
    public bool DisableSeeder { get; set; }
    public bool DisableAlerter { get; set; }
    public bool DisableSlackAlerts { get; set; }

    internal TaskCompletionSource? OrchestratorStartSignal { get; set; }
}

internal sealed class DbConfiguration
{
    public string Host { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string Database { get; set; } = null!;
    public int Port { get; set; } = 5432;
}
