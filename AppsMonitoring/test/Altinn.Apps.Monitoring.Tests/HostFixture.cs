using System.Globalization;
using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using WireMock.Server;

namespace Altinn.Apps.Monitoring.Tests;

internal sealed class HostFixture : WebApplicationFactory<Program>
{
    public PostgreSqlContainer PostgreSqlContainer { get; }
    public WireMockServer MockServer { get; }

    private readonly Action<IServiceCollection, HostFixture>? _configureServices;

    public FakeTimeProvider TimeProvider =>
        Services.GetRequiredService<TimeProvider>() as FakeTimeProvider
        ?? throw new InvalidOperationException("TimeProvider is not FakeTimeProvider");

    public Seeder Seeder => Services.GetRequiredService<Seeder>();

    public Repository Repository => Services.GetRequiredService<Repository>();

    public Orchestrator Orchestrator => Services.GetRequiredService<Orchestrator>();

    public IAlerter Alerter => Services.GetRequiredService<IAlerter>();

    public IQueryLoader QueryLoader => Services.GetRequiredService<IQueryLoader>();

    public IHostApplicationLifetime Lifetime => Services.GetRequiredService<IHostApplicationLifetime>();

    private HostFixture(
        PostgreSqlContainer postgreSqlContainer,
        WireMockServer mockServer,
        Action<IServiceCollection, HostFixture>? configureServices
    )
    {
        PostgreSqlContainer = postgreSqlContainer;
        MockServer = mockServer;
        _configureServices = configureServices;
    }

    public async Task<HttpClient> Start(CancellationToken cancellationToken)
    {
        var client = CreateClient();
        try
        {
            var response = await client.GetStringAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
            Assert.Equal("Healthy", response);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
            // logging.AddXunit();
        });
        builder.ConfigureHostConfiguration(config =>
        {
            var connnStringBuilder = new NpgsqlConnectionStringBuilder(PostgreSqlContainer.GetConnectionString());
            var dbAdmin = $"{nameof(AppConfiguration)}:{nameof(AppConfiguration.DbAdmin)}";
            var db = $"{nameof(AppConfiguration)}:{nameof(AppConfiguration.Db)}";
            var port = connnStringBuilder.Port.ToString(CultureInfo.InvariantCulture);
            config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["IsTest"] = "true",

                    [$"{dbAdmin}:{nameof(DbConfiguration.Host)}"] = connnStringBuilder.Host,
                    [$"{dbAdmin}:{nameof(DbConfiguration.Username)}"] = connnStringBuilder.Username,
                    [$"{dbAdmin}:{nameof(DbConfiguration.Password)}"] = connnStringBuilder.Password,
                    [$"{dbAdmin}:{nameof(DbConfiguration.Database)}"] = connnStringBuilder.Database,
                    [$"{dbAdmin}:{nameof(DbConfiguration.Port)}"] = port,

                    [$"{db}:{nameof(DbConfiguration.Host)}"] = connnStringBuilder.Host,
                    [$"{db}:{nameof(DbConfiguration.Username)}"] = "platform_monitoring",
                    [$"{db}:{nameof(DbConfiguration.Password)}"] = connnStringBuilder.Password,
                    [$"{db}:{nameof(DbConfiguration.Database)}"] = connnStringBuilder.Database,
                    [$"{db}:{nameof(DbConfiguration.Port)}"] = port,
                }
            );
        });
        builder.ConfigureServices(ConfigureServices);

        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await PostgreSqlContainer.DisposeAsync();
        MockServer.Dispose();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(MockServer);
        services.Configure<AppConfiguration>(options =>
        {
            options.DisableOrchestrator = true;
            options.DisableSeeder = true;
            options.DisableAlerter = true;
            options.DisableSlackAlerts = true;
        });

        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(timeProvider);

        _configureServices?.Invoke(services, this);
    }

    private static string FindSolutionDir()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (Directory.GetFiles(dir, "*.sln").Length != 0)
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new Exception("Solution directory not found");
    }

    public static async Task<HostFixture> Create(Action<IServiceCollection, HostFixture>? configureServices = null)
    {
        var solutionDir = FindSolutionDir();

        var cancellationToken = TestContext.Current.CancellationToken;

        PostgreSqlContainer? postgreSqlContainer = null;
        HostFixture? fixture = null;
        try
        {
            var initFile = new FileInfo(Path.Combine(solutionDir, "infra", "postgres_init.sql"));
            var dbFile = new FileInfo(
                Path.Combine(solutionDir, "test", "Altinn.Apps.Monitoring.Tests", "data", "mini.db")
            );
            Assert.True(initFile.Exists, "Postgres init file not found at: " + initFile.FullName);
            Assert.True(dbFile.Exists, "Db seed file not found at: " + dbFile.FullName);
            postgreSqlContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithUsername("platform_monitoring_admin")
                .WithPassword("Password")
                .WithResourceMapping(initFile, "/docker-entrypoint-initdb.d/")
                .WithResourceMapping(dbFile, "/seed/")
                .WithDatabase("monitordb")
                // We reset the environment as we don't want postgresql to actually create
                // the database for us, as we are providing our own init script.
                // .WithAutoRemove(false)
                // .WithCleanUp(false)
                .WithEnvironment("POSTGRES_DB", PostgreSqlBuilder.DefaultDatabase)
                .WithWaitStrategy(
                    Wait.ForUnixContainer().AddCustomWaitStrategy(new WaitUntil("monitordb", "platform_monitoring"))
                )
                .Build();
            await postgreSqlContainer.StartAsync(cancellationToken);

            var server = WireMockServer.Start();

            fixture = new HostFixture(postgreSqlContainer, server, configureServices);

            return fixture;
        }
        catch (Exception)
        {
            if (fixture != null)
                await fixture.DisposeAsync();
            if (postgreSqlContainer != null)
                await postgreSqlContainer.DisposeAsync();

            throw;
        }
    }

    private sealed class WaitUntil : IWaitUntil
    {
        private readonly IList<string> _command;

        public WaitUntil(string database, string username)
        {
            // Explicitly specify the host to ensure readiness only after the initdb scripts have executed, and the server is listening on TCP/IP.
            _command = new List<string>
            {
                "pg_isready",
                "--host",
                "localhost",
                "--dbname",
                database,
                "--username",
                username,
            };
        }

        public async Task<bool> UntilAsync(IContainer iContainer)
        {
            var container = (PostgreSqlContainer)iContainer;
            var execResult = await container.ExecAsync(_command).ConfigureAwait(false);

            if (execResult.Stderr.Contains("pg_isready was not found", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"The '{container.Image.FullName}' image does not contain: pg_isready. Please use 'postgres:9.3' onwards."
                );
            }

            if (execResult.ExitCode != 0L)
                return false;

            var result = await container.ExecScriptAsync(
                """
                    CREATE TABLE IF NOT EXISTS seed(
                        id  integer PRIMARY KEY GENERATED BY DEFAULT AS IDENTITY,
                        data bytea NOT NULL
                    );
                    INSERT INTO seed(data) VALUES (pg_read_binary_file('/seed/mini.db'));
                """
            );
            Assert.Equal(0L, result.ExitCode);
            return true;
        }
    }
}
