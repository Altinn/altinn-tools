using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Altinn.Apps.Monitoring.Application.Db;
using Altinn.Apps.Monitoring.Domain;
using Microsoft.Extensions.Options;

namespace Altinn.Apps.Monitoring.Application;

internal readonly record struct OrchestratorEvent(
    ServiceOwner ServiceOwner,
    Query Query,
    Instant SearchFrom,
    Instant SearchTo,
    IReadOnlyList<TelemetryEntity> Telemetry,
    InsertTelemetryResult Result
);

internal sealed class Orchestrator(
    ILogger<Orchestrator> logger,
    IOptionsMonitor<AppConfiguration> appConfiguration,
    IServiceOwnerDiscovery serviceOwnerDiscovery,
    IServiceOwnerLogsAdapter serviceOwnerLogs,
    IHostApplicationLifetime applicationLifetime,
    Repository repository,
    IQueryLoader queryLoader,
    TimeProvider timeProvider,
    Telemetry telemetry
) : IApplicationService, IDisposable
{
    private readonly ILogger<Orchestrator> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _appConfiguration = appConfiguration;
    private readonly IServiceOwnerDiscovery _serviceOwnerDiscovery = serviceOwnerDiscovery;
    private readonly IServiceOwnerLogsAdapter _serviceOwnerLogs = serviceOwnerLogs;
    private readonly IHostApplicationLifetime _lifetime = applicationLifetime;
    private readonly Repository _repository = repository;
    private readonly IQueryLoader _queryLoader = queryLoader;
    private readonly TimeProvider _timeProvider = timeProvider;
#pragma warning disable CA2213 // Disposable fields should be disposed
    // DI container owns telemetry
    private readonly Telemetry _telemetry = telemetry;
#pragma warning restore CA2213 // Disposable fields should be disposed

    private Task? _serviceOwnerDiscoveryThread;
    private readonly SemaphoreSlim _semaphore = new(10, 10);
    private ConcurrentDictionary<ServiceOwner, Task> _serviceOwnerThreads = new();
    private Channel<OrchestratorEvent>? _events;

    public ChannelReader<OrchestratorEvent> Events =>
        _events?.Reader ?? throw new InvalidOperationException("Not started");

    private CancellationTokenSource? _cancellationTokenSource;

    public Task Start(CancellationToken cancellationToken)
    {
        using var activity = _telemetry.Activities.StartActivity("Orchestrator.Start");

        _events = Channel.CreateBounded<OrchestratorEvent>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            }
        );
        if (_appConfiguration.CurrentValue.DisableOrchestrator)
        {
            _logger.LogInformation("Orchestrator disabled");
            _events.Writer.Complete();
            return Task.CompletedTask;
        }
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.ApplicationStopping
        );
        cancellationToken = _cancellationTokenSource.Token;

        _serviceOwnerDiscoveryThread = Task.Run(
            () => ServiceOwnerDiscoveryThread(_cancellationTokenSource.Token),
            cancellationToken
        );
        return Task.CompletedTask;
    }

    private async Task ServiceOwnerDiscoveryThread(CancellationToken cancellationToken)
    {
        var startActivity = _telemetry.Activities.StartRootActivity("Orchestrator.ServiceOwnerDiscoveryThread.Start");

        var options = _appConfiguration.CurrentValue;
        if (options.OrchestratorStartSignal is not null)
            await options.OrchestratorStartSignal.Task;

        try
        {
            using var timer = new PeriodicTimer(options.PollInterval, _timeProvider);

            startActivity?.Dispose();
            startActivity = null;
            do
            {
                using var activity = _telemetry.Activities.StartRootActivity(
                    "Orchestrator.ServiceOwnerDiscoveryThread.Iteration"
                );

                var serviceOwners = await _serviceOwnerDiscovery.Discover(cancellationToken);

                foreach (var serviceOwner in serviceOwners)
                {
                    _ = _serviceOwnerThreads.GetOrAdd(
                        serviceOwner,
                        (serviceOwner, cancellationToken) =>
                        {
                            _logger.LogInformation("Starting service owner thread for {ServiceOwner}", serviceOwner);
                            return Task.Run(
                                () => ServiceOwnerThread(serviceOwner, cancellationToken),
                                cancellationToken
                            );
                        },
                        cancellationToken
                    );
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service owner discovery thread cancelled");
        }
        catch (Exception ex)
        {
            startActivity?.AddException(ex);
            _logger.LogError(ex, "Service owner discovery thread failed");
            _lifetime.StopApplication();
        }
        finally
        {
            startActivity?.Dispose();
        }
    }

    private async Task ServiceOwnerThread(ServiceOwner serviceOwner, CancellationToken cancellationToken)
    {
        var startActivity = _telemetry.Activities.StartRootActivity("Orchestrator.ServiceOwnerThread.Start");
        startActivity?.SetTag("serviceowner", serviceOwner.Value);

        var options = _appConfiguration.CurrentValue;

        if (options.OrchestratorStartSignal is not null)
            await options.OrchestratorStartSignal.Task;

        using var timer = new PeriodicTimer(options.PollInterval, _timeProvider);

        try
        {
            _logger.LogInformation("[{ServiceOwner}] starting querying thread", serviceOwner);

            startActivity?.Dispose();
            startActivity = null;
            do
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    await ServiceOwnerThreadIteration(serviceOwner, options, cancellationToken);
                }
                finally
                {
                    _semaphore.Release();
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{ServiceOwner}] querying thread cancelled", serviceOwner);
        }
        catch (Exception ex)
        {
            startActivity?.AddException(ex);
            _logger.LogError(ex, "[{ServiceOwner}] thread failed, crashing..", serviceOwner);
            _lifetime.StopApplication();
        }
        finally
        {
            startActivity?.Dispose();
        }
    }

    private async Task ServiceOwnerThreadIteration(
        ServiceOwner serviceOwner,
        AppConfiguration options,
        CancellationToken cancellationToken
    )
    {
        using var activity = _telemetry.Activities.StartRootActivity("Orchestrator.ServiceOwnerThread.Iteration");
        activity?.SetTag("serviceowner", serviceOwner.Value);

        IReadOnlyList<Query> queries;
        try
        {
            queries = await _queryLoader.Load(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[{ServiceOwner}] failed loading queries, trying again soon...", serviceOwner);
            return;
        }
        foreach (var query in queries)
        {
            using var queryActivity = _telemetry.Activities.StartActivity("Orchestrator.ServiceOwnerThread.Query");
            queryActivity?.SetTag("query", query.Name);
            try
            {
                var queryStates = await _repository.ListQueryStates(serviceOwner, query, cancellationToken);
                var queryState = queryStates.SingleOrDefault();

                var searchTimestamp = _timeProvider.GetCurrentInstant();
                var searchFrom =
                    queryState?.QueriedUntil
                    ?? searchTimestamp.Minus(Duration.FromDays(options.SearchFromDays)).Minus(Duration.FromSeconds(1));
                var searchTo = _timeProvider.GetCurrentInstant().Minus(Duration.FromMinutes(10));

                _logger.LogInformation(
                    "[{ServiceOwner}] querying '{Query}' from {SearchFrom} to {SearchTo}",
                    serviceOwner,
                    query.Name,
                    searchFrom,
                    searchTo
                );

                var tables = await _serviceOwnerLogs.Query(
                    serviceOwner,
                    query,
                    searchFrom,
                    searchTo,
                    cancellationToken: cancellationToken
                );

                var totalRows = tables.Sum(t => t.Count);
                if (totalRows > 0)
                {
                    _logger.LogInformation(
                        "[{ServiceOwner}] found {TotalRows} rows in {TableCount} tables",
                        serviceOwner,
                        totalRows,
                        tables.Count
                    );
                }

                var ingestionTimestamp = _timeProvider.GetCurrentInstant();
                List<TelemetryEntity> telemetry = new(totalRows);
                var positionByExtId = new Dictionary<string, int>(totalRows).GetAlternateLookup<ReadOnlySpan<char>>();
                foreach (var table in tables)
                {
                    foreach (var row in table)
                    {
                        if (positionByExtId.TryGetValue(row.ExtId, out var index))
                        {
                            _logger.LogWarning(
                                "[{ServiceOwner}] found duplicate telemetry entry for {ExtId}",
                                serviceOwner,
                                row.ExtId
                            );
                            var existing = telemetry[index];
                            var shouldReplace = (row.Data, existing.Data) switch
                            {
                                (TraceData @new, TraceData old) => @new.Duration > old.Duration,
                                _ => throw new NotSupportedException(),
                            };
                            if (shouldReplace)
                                telemetry[index] = row with { TimeIngested = ingestionTimestamp };
                        }
                        else
                        {
                            positionByExtId.Dictionary.Add(row.ExtId, telemetry.Count);
                            telemetry.Add(row with { TimeIngested = ingestionTimestamp });
                        }
                    }
                }

                Debug.Assert(
                    telemetry.GroupBy(t => t.ExtId).All(g => g.Count() == 1),
                    "Should have gotten rid of dupes"
                );

                var result = await _repository.InsertTelemetry(
                    serviceOwner,
                    query,
                    searchTo,
                    telemetry,
                    cancellationToken
                );

                Debug.Assert(_events is not null);
                await _events.Writer.WriteAsync(
                    new OrchestratorEvent(serviceOwner, query, searchFrom, searchTo, telemetry, result),
                    cancellationToken
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                queryActivity?.AddException(ex);
                _logger.LogError(ex, "[{ServiceOwner}] failed to query, trying again soon...", serviceOwner);
            }
        }
    }

    public async Task Stop()
    {
        if (_serviceOwnerDiscoveryThread is not null)
            await _serviceOwnerDiscoveryThread;

        foreach (var (_, thread) in _serviceOwnerThreads)
            await thread;

        if (_events is not null)
            _events.Writer.TryComplete();
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
        _semaphore.Dispose();
    }
}
