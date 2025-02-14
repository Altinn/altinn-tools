using Altinn.Apps.Monitoring.Application;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Altinn.Apps.Monitoring.Tests;

internal sealed class HostFixture : WebApplicationFactory<Program>
{
    public PostgreSqlContainer PostgreSqlContainer { get; }

    private HostFixture(PostgreSqlContainer postgreSqlContainer)
    {
        PostgreSqlContainer = postgreSqlContainer;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{nameof(AppConfiguration)}:{nameof(AppConfiguration.DbConnectionString)}"] =
                        PostgreSqlContainer.GetConnectionString(),
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
    }

    private void ConfigureServices(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ImplementationType == typeof(Orchestrator));
        if (descriptor != null)
            services.Remove(descriptor);
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

    public static async Task<HostFixture> Create()
    {
        var solutionDir = FindSolutionDir();

        var cancellationToken = TestContext.Current.CancellationToken;

        PostgreSqlContainer? postgreSqlContainer = null;
        HostFixture? fixture = null;
        try
        {
            var initFile = new FileInfo(Path.Combine(solutionDir, "infra", "postgres_init.sql"));
            Assert.True(initFile.Exists, "Postgres init file not found at: " + initFile.FullName);
            postgreSqlContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithUsername("platform_monitoring_admin")
                .WithPassword("Password")
                .WithResourceMapping(initFile, "/docker-entrypoint-initdb.d/")
                .WithDatabase("monitoringdb")
                // We reset the environment as we don't want postgresql to actually create
                // the database for us, as we are providing our own init script.
                .WithEnvironment("POSTGRES_DB", PostgreSqlBuilder.DefaultDatabase)
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .AddCustomWaitStrategy(new WaitUntil("monitoringdb", "platform_monitoring_admin"))
                )
                .Build();
            await postgreSqlContainer.StartAsync(cancellationToken);

            fixture = new HostFixture(postgreSqlContainer);

            using var client = fixture.CreateClient();
            Assert.Equal("Healthy", await client.GetStringAsync("/health", cancellationToken));

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

        public async Task<bool> UntilAsync(IContainer container)
        {
            var execResult = await container.ExecAsync(_command).ConfigureAwait(false);

            if (execResult.Stderr.Contains("pg_isready was not found"))
            {
                throw new NotSupportedException(
                    $"The '{container.Image.FullName}' image does not contain: pg_isready. Please use 'postgres:9.3' onwards."
                );
            }

            return 0L.Equals(execResult.ExitCode);
        }
    }
}
