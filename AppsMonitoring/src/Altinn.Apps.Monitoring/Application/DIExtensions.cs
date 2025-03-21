using System.Diagnostics;
using Altinn.Apps.Monitoring.Application.Azure;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Application.DbUp;
using Altinn.Apps.Monitoring.Application.Slack;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Altinn.Apps.Monitoring.Application;

internal static class DIExtensions
{
    public static IHostApplicationBuilder AddApplication(this IHostApplicationBuilder builder)
    {
        builder.AddConfig();
        builder.AddOtel();

        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // Hosted service registration
        // 1. Migrate db
        builder.Services.AddHostedService<Migrator>();
        // 2. Seed db
        builder.Services.TryAddSingleton<Seeder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Seeder>());
        // 3. Run the orchestrator (main loop)
        builder.Services.TryAddSingleton<Orchestrator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Orchestrator>());
        // 4. Slack alerting
        builder.Services.TryAddSingleton<IAlerter, SlackAlerter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IAlerter>());

        builder.AddDb();

        builder.Services.TryAddSingleton<Repository>();
        builder.Services.TryAddSingleton<DistributedLocking>();

        // Azure services/infra
        builder.Services.AddHybridCache();
        builder.Services.TryAddSingleton<AzureClients>();
        builder.Services.TryAddSingleton<AzureServiceOwnerResources>();
        builder.Services.TryAddSingleton<IServiceOwnerDiscovery, AzureServiceOwnerDiscovery>();
        builder.Services.TryAddSingleton<AzureServiceOwnerMonitorAdapter>();
        builder.Services.TryAddSingleton<IServiceOwnerLogsAdapter>(sp =>
            sp.GetRequiredService<AzureServiceOwnerMonitorAdapter>()
        );
        builder.Services.TryAddSingleton<IServiceOwnerTraceAdapter>(sp =>
            sp.GetRequiredService<AzureServiceOwnerMonitorAdapter>()
        );
        builder.Services.TryAddSingleton<IServiceOwnerMetricsAdapter>(sp =>
            sp.GetRequiredService<AzureServiceOwnerMonitorAdapter>()
        );
        builder.Services.TryAddSingleton<IQueryLoader, StaticQueryLoader>();

        return builder;
    }

    private static IHostApplicationBuilder AddConfig(this IHostApplicationBuilder builder)
    {
        if (!builder.IsTest())
        {
            builder.Configuration.AddJsonFile("appsettings.Secret.json", optional: true, reloadOnChange: true);
            builder.Configuration.AddJsonFile("config/appsettings.Secret.json", optional: true, reloadOnChange: true);
        }

        builder
            .Services.AddOptions<AppConfiguration>()
            .BindConfiguration(nameof(AppConfiguration))
            .Validate(config =>
            {
                if (config.PollInterval <= TimeSpan.Zero)
                    return false;
                if (config.SearchFromDays <= 0)
                    return false;
                if (string.IsNullOrWhiteSpace(config.AltinnEnvironment))
                    return false;
                if (config.AltinnEnvironment is not "at24" and not "tt02" and not "prod")
                    return false;
                if (string.IsNullOrWhiteSpace(config.Db?.Host))
                    return false;
                if (string.IsNullOrWhiteSpace(config.Db?.Username))
                    return false;
                if (string.IsNullOrWhiteSpace(config.Db?.Password))
                    return false;
                if (string.IsNullOrWhiteSpace(config.Db?.Database))
                    return false;
                if (string.IsNullOrWhiteSpace(config.DbAdmin?.Host))
                    return false;
                if (string.IsNullOrWhiteSpace(config.DbAdmin?.Username))
                    return false;
                if (string.IsNullOrWhiteSpace(config.DbAdmin?.Password))
                    return false;
                if (string.IsNullOrWhiteSpace(config.DbAdmin?.Database))
                    return false;

                return true;
            })
            .ValidateOnStart();

        if (!builder.IsLocal())
        {
            var keyVaultUri = builder.Configuration.GetSection(nameof(AppConfiguration))[
                nameof(AppConfiguration.KeyVaultUri)
            ];
            if (string.IsNullOrWhiteSpace(keyVaultUri))
                throw new InvalidOperationException("Missing key vault URI in configuration");

            builder.Configuration.AddAzureKeyVault(
                new Uri(keyVaultUri),
                AzureClients.CreateCredential(builder.Environment),
                new AzureKeyVaultConfigurationOptions { ReloadInterval = TimeSpan.FromMinutes(5) }
            );
        }

        return builder;
    }

    private static IHostApplicationBuilder AddOtel(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<Telemetry>();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();

        var otel = builder.Services.AddOpenTelemetry();

        otel.WithMetrics(metrics =>
        {
            metrics.AddMeter("System.Runtime");
            metrics.AddMeter("Microsoft.AspNetCore.Hosting");
            metrics.AddMeter("Microsoft-Extensions-HybridCache");
            metrics.AddMeter("System.Net.Http");
            metrics.AddNpgsqlInstrumentation();
            // No exporter yet in real envs
        });
        otel.WithTracing(traces =>
        {
            traces.AddSource(Telemetry.ActivitySourceName);
            traces.AddSource("Azure.*");
            traces.AddAspNetCoreInstrumentation();
            traces.AddHttpClientInstrumentation(o =>
                o.FilterHttpRequestMessage = (_) =>
                {
                    // Taken from: https://github.com/Azure/azure-sdk-for-net/blob/ce26920571d07a97c2834867bf3f09a651ac3eee/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/OpenTelemetryBuilderExtensions.cs#L102
                    var parentActivity = Activity.Current?.Parent;
                    if (
                        parentActivity is not null
                        && parentActivity.Source.Name.Equals("Azure.Core.Http", StringComparison.Ordinal)
                    )
                    {
                        return false;
                    }
                    return true;
                }
            );
            traces.AddNpgsql();
            if (!builder.IsTest())
                traces.AddOtlpExporter();
        });
        otel.WithLogging(
            logging =>
            {
                if (!builder.IsTest())
                    logging.AddOtlpExporter();
            },
            options => options.IncludeFormattedMessage = true
        );

        return builder;
    }

    private static IHostApplicationBuilder AddDb(this IHostApplicationBuilder builder)
    {
        static ConnectionString BuildConnectionString(IHostApplicationBuilder builder, DbConfiguration db)
        {
            NpgsqlConnectionStringBuilder connStringBuilder = new();
            connStringBuilder.Host = db.Host;
            connStringBuilder.Username = db.Username;
            connStringBuilder.Password = db.Password;
            connStringBuilder.Database = db.Database;
            connStringBuilder.Port = db.Port;
            connStringBuilder.IncludeErrorDetail = true;
            if (!builder.IsLocal())
                connStringBuilder.SslMode = SslMode.Require;

            return new ConnectionString(connStringBuilder.ToString());
        }

        builder.Services.TryAddKeyedSingleton<ConnectionString>(
            Config.UserMode,
            (sp, key) => BuildConnectionString(builder, sp.GetRequiredService<IOptions<AppConfiguration>>().Value.Db)
        );

        builder.Services.TryAddKeyedSingleton<ConnectionString>(
            Config.AdminMode,
            (sp, key) =>
                BuildConnectionString(builder, sp.GetRequiredService<IOptions<AppConfiguration>>().Value.DbAdmin)
        );

        static NpgsqlDataSource BuildDataSource(IServiceProvider sp, object? key) =>
            new NpgsqlDataSourceBuilder(sp.GetRequiredKeyedService<ConnectionString>(key).Value)
                .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
                .EnableDynamicJson()
                .ConfigureJsonOptions(Db.Config.JsonOptions)
                .UseNodaTime()
                .ConfigureTracing(o => { })
                .Build();
        builder.Services.AddKeyedSingleton<NpgsqlDataSource>(Config.UserMode, (sp, key) => BuildDataSource(sp, key));
        builder.Services.AddKeyedSingleton<NpgsqlDataSource>(Config.AdminMode, (sp, key) => BuildDataSource(sp, key));

        return builder;
    }

    public static bool IsTest(this IHostApplicationBuilder builder) => builder.Configuration.GetValue<bool>("IsTest");

    public static bool IsLocal(this IHostApplicationBuilder builder) =>
        builder.Environment.IsDevelopment() || builder.IsTest();
}
