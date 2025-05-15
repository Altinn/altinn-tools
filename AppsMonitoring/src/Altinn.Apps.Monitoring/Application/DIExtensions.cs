using System.Diagnostics;
using System.Runtime.InteropServices;
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

internal sealed class LeaderService(
    ILogger<LeaderService> logger,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    DistributedLocking locking,
    IServiceProvider serviceProvider
) : BackgroundService
{
    private readonly ILogger<LeaderService> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly DistributedLocking _locking = locking;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _tcs = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public void SignalStop()
    {
        _cts.Cancel();
        _tcs.TrySetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Leader service started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Attempting to become leader");
                await using var handle = await _locking.TryAcquireLock(DistributedLockName.Application, stoppingToken);
                if (handle is null)
                {
                    _logger.LogInformation("Failed to become leader, waiting...");
                    await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, stoppingToken);
                    continue;
                }

                _logger.LogInformation("Became leader");

                var services = _serviceProvider.GetServices<IApplicationService>().ToArray();
                foreach (var service in services)
                {
                    _logger.LogInformation("Starting service {Service}", service.GetType().Name);
                    await service.Start(_cts.Token);
                }

                await _tcs.Task;

                foreach (var service in services.Reverse())
                {
                    _logger.LogInformation("Stopping service {Service}", service.GetType().Name);
                    try
                    {
                        await service.Stop();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Failed to stop service {Service}", service.GetType().Name);
                    }
                }

                break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Leader service was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Leader service failed");
            _lifetime.StopApplication();
        }
    }

    public override void Dispose()
    {
        _cts.Dispose();
        base.Dispose();
    }
}

internal interface IApplicationService
{
    Task Start(CancellationToken cancellationToken);

    Task Stop();
}

internal static class DIExtensions
{
    public static IHostApplicationBuilder AddApplication(this IHostApplicationBuilder builder)
    {
        builder.AddConfig();
        builder.AddOtel();

        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        if (!builder.IsLocal())
        {
            builder.Services.AddSingleton<IHostLifetime>(sp =>
                ActivatorUtilities.CreateInstance<DelayedShutdownHostLifetime>(sp, TimeSpan.FromSeconds(5))
            );
        }

        // Hosted service registration
        // 1. Migrate db
        builder.Services.AddHostedService<Migrator>();
        // 2. Seed db
        builder.Services.TryAddSingleton<Seeder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Seeder>());
        // 3. Leader service
        builder.Services.AddSingleton<LeaderService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LeaderService>());
        // 3. Run the orchestrator (main loop)
        builder.Services.TryAddSingleton<Orchestrator>();
        builder.Services.AddSingleton<IApplicationService>(sp => sp.GetRequiredService<Orchestrator>());
        // 4. Slack alerting
        builder.Services.TryAddSingleton<IAlerter, SlackAlerter>();
        builder.Services.AddSingleton<IApplicationService>(sp => sp.GetRequiredService<IAlerter>());

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
            traces.AddAspNetCoreInstrumentation(options =>
            {
                options.Filter = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                };
            });
            traces.AddHttpClientInstrumentation(options =>
                options.FilterHttpRequestMessage = (_) =>
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
            {
                connStringBuilder.SslMode = SslMode.Require;
                connStringBuilder.KeepAlive = 30;
                connStringBuilder.MaxPoolSize = 40; // Azure PG is sized to 50
            }

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

    internal sealed class DelayedShutdownHostLifetime(
        ILogger<DelayedShutdownHostLifetime> logger,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        LeaderService leaderService,
        TimeSpan delay
    ) : IHostLifetime, IDisposable
    {
        private readonly ILogger<DelayedShutdownHostLifetime> _logger = logger;
        private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
        private readonly TimeProvider _timeProvider = timeProvider;
#pragma warning disable CA2213 // Disposable fields should be disposed
        // Owned by DI container
        private readonly LeaderService _leaderService = leaderService;
#pragma warning restore CA2213 // Disposable fields should be disposed
        private readonly TimeSpan _delay = delay;
        private IEnumerable<IDisposable>? _disposables;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting, will delay OS shutdown request by {Delay} when requested", _delay);
            _disposables =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGQUIT, HandleSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleSignal),
            ];
            _applicationLifetime.ApplicationStopping.Register(() => _leaderService.SignalStop());
            return Task.CompletedTask;
        }

        private void HandleSignal(PosixSignalContext ctx)
        {
            _logger.LogInformation(
                "Received signal {Signal}, shutting down (after delay: {Delay})",
                ctx.Signal,
                _delay
            );
            ctx.Cancel = true;
            // This will effectively stop the background processing/leadership lease a
            // little earlier (before the whole application stops)
            _leaderService.SignalStop();
            Task.Delay(_delay, _timeProvider)
                .ContinueWith(t => _applicationLifetime.StopApplication(), TaskScheduler.Default);
        }

        public void Dispose()
        {
            _logger.LogInformation("Disposing...");
            foreach (var disposable in _disposables ?? Enumerable.Empty<IDisposable>())
            {
                disposable.Dispose();
            }
        }
    }
}
