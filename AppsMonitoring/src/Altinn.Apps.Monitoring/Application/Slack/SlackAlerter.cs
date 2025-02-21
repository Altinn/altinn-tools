using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Altinn.Apps.Monitoring.Application.Db;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Altinn.Apps.Monitoring.Application.Slack;

internal sealed class SlackAlerter(
    ILogger<SlackAlerter> logger,
    IOptionsMonitor<AppConfiguration> config,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    DistributedLocking locking,
    Repository repository
) : IAlerter, IDisposable
{
    private readonly HttpClient _httpClient = new HttpClient(
        new ResilienceHandler(
            new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddRetry(
                    new HttpRetryStrategyOptions { BackoffType = DelayBackoffType.Exponential, MaxRetryAttempts = 3 }
                )
                .Build()
        )
        {
            InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) },
        }
    );

    private readonly ILogger<SlackAlerter> _logger = logger;
    private readonly IOptionsMonitor<AppConfiguration> _config = config;
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly DistributedLocking _locking = locking;
    private readonly Repository _repository = repository;

    private Channel<AlerterEvent>? _results;

    public ChannelReader<AlerterEvent> Results =>
        _results?.Reader ?? throw new InvalidOperationException("Not started");

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _thread;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _results = Channel.CreateBounded<AlerterEvent>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
            }
        );

        var config = _config.CurrentValue;
        if (config.DisableAlerter)
        {
            _logger.LogInformation("Alerter disabled");
            return Task.CompletedTask;
        }

        var slackAccessToken = config.SlackAccessToken;
        _httpClient.BaseAddress = new(config.SlackHost);
        _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", slackAccessToken);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.ApplicationStopping
        );
        cancellationToken = _cancellationTokenSource.Token;
        _thread = Task.Run(() => Thread(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task Thread(CancellationToken cancellationToken)
    {
        var config = _config.CurrentValue;
        try
        {
            await using var handle = await locking.Lock(DistributedLockName.Alerter, cancellationToken);
            if (handle.HandleLostToken.CanBeCanceled)
            {
                _logger.LogInformation("Will monitor for lost alerter lock");
                handle.HandleLostToken.Register(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return; // We are already shutting down
                    _logger.LogError("Alerter lock lost, stopping application");
                    _lifetime.StopApplication();
                });
            }

            using var timer = new PeriodicTimer(config.PollInterval / 2, _timeProvider);

            do
            {
                var workItems = await _repository.ListAlerterWorkItems(AlertData.Types.Slack, cancellationToken);
                if (workItems.Length == 0)
                    continue;

                Debug.Assert(_results is not null);

                for (int i = 0; i < workItems.Length; i++)
                {
                    var (item, alert) = workItems[i];
                    try
                    {
                        if (alert is null)
                        {
                            alert = new AlertEntity
                            {
                                Id = 0,
                                State = AlertState.Pending,
                                TelemetryId = item.Id,
                                Data = new SlackAlertData
                                {
                                    Channel = null,
                                    Message = null,
                                    ThreadTs = null,
                                },
                            };
                            workItems[i] = (item, alert);
                        }

                        while (alert.State < AlertState.Mitigated)
                        {
                            var updatedAlert = await ProgressAlert(item, alert, cancellationToken);
                            if (updatedAlert is null)
                                break; // Try again later
                            await _repository.SaveAlert(updatedAlert, cancellationToken);

                            await _results.Writer.WriteAsync(
                                new AlerterEvent
                                {
                                    Item = item,
                                    AlertBefore = alert,
                                    AlertAfter = updatedAlert,
                                },
                                cancellationToken
                            );
                            alert = updatedAlert;
                            workItems[i] = (item, alert);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(
                            ex,
                            "Failed to process alerter work item, TelemetryId='{TelemetryId}', AlertId='{}'",
                            item.Id,
                            alert?.Id
                        );
                    }
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Alerter thread cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alerter thread failed");
            _lifetime.StopApplication();
        }
    }

    private async Task<AlertEntity?> ProgressAlert(
        TelemetryEntity item,
        AlertEntity alert,
        CancellationToken cancellationToken
    )
    {
        if (alert.Data is not SlackAlertData)
            throw new InvalidOperationException("Unexpected alert data type: " + alert.Data?.GetType());
        switch (alert.State)
        {
            case AlertState.Pending:
                return await HandlePendingAlert(item, alert, cancellationToken);
            case AlertState.Alerted:
            {
                if (alert.Data is null)
                    throw new InvalidOperationException("Missing Slack thread timestamp");
                // TODO: mitigate
                return null;
            }
            case AlertState.Mitigated:
                return null; // Nothing left to do
            default:
                throw new InvalidOperationException($"Unknown alert state: {alert.State}");
        }
    }

    private async Task<AlertEntity?> HandlePendingAlert(
        TelemetryEntity item,
        AlertEntity alert,
        CancellationToken cancellationToken
    )
    {
        if (alert.Data is not SlackAlertData alertData)
            throw new InvalidOperationException("Unexpected alert data type: " + alert.Data?.GetType());
        if (alertData.ThreadTs is not null)
            throw new InvalidOperationException(
                "Unexpected Slack thread timestamp - we should not have pending alerts with a thread timestamp"
            );

        switch (item.Data)
        {
            case TraceData data:
            {
                var text = $"""
                    *ALERT* `{item.TimeGenerated}`:
                    - App: *{item.ServiceOwner}*/*{item.AppName}*/*{item.AppVersion}*
                    - Feil: *{data.SpanName}* (status *{data.Result}*, *{data.Duration.TotalMilliseconds:0.00}ms*)
                    - Instansen: *{data.InstanceOwnerPartyId}*/*{data.InstanceId}*
                    - Operation ID: *{data.TraceId}*
                    """;
                var channel = _config.CurrentValue.SlackChannel;
                using var response = await _httpClient.PostAsJsonAsync(
                    "/api/chat.postMessage",
                    new
                    {
                        channel = _config.CurrentValue.SlackChannel,
                        text = text,
                        mrkdwn = true,
                        // thread_ts - we will use this to update the Slack alert when mitigations have been made (not here though)
                    },
                    cancellationToken
                );

                var slackResponse = await response.Content.ReadFromJsonAsync<SlackResponse>(cancellationToken);

                if (slackResponse is SlackErrorResponse error)
                {
                    _logger.LogError("Failed to send alert to Slack: {Error}", error.Error);
                    return null;
                }
                else if (slackResponse is SlackOkResponse ok)
                {
                    _logger.LogInformation(
                        "Alert sent to Slack: '{Ts}' for telemetry ID '{TelemetryId}'",
                        ok.Ts,
                        item.Id
                    );
                    return alert with
                    {
                        State = AlertState.Alerted,
                        Data = alertData with { Message = text, Channel = channel, ThreadTs = ok.Ts },
                    };
                }
                else
                    throw new InvalidOperationException($"Unknown Slack response: {slackResponse}");
            }
            default:
                throw new InvalidOperationException($"Unknown telemetry data: {item.Data}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_thread is not null)
            await _thread;

        if (_results is not null)
            _results.Writer.TryComplete();
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
    }

    internal sealed record SlackAlertData : AlertData
    {
        public required string? Channel { get; init; }

        public required string? Message { get; init; }

        public required string? ThreadTs { get; init; }
    }

    [JsonConverter(typeof(SlackResponseJsonConverter))]
    internal abstract record SlackResponse
    {
        [JsonPropertyName("ok")]
        public required bool Ok { get; init; }
    }

    internal sealed record SlackOkResponse : SlackResponse
    {
        [JsonPropertyName("ts")]
        public required string? Ts { get; init; }

        [JsonPropertyName("channel")]
        public required string? Channel { get; init; }
    }

    internal sealed record SlackErrorResponse : SlackResponse
    {
        [JsonPropertyName("error")]
        public required string? Error { get; init; }
    }

    internal sealed class SlackResponseJsonConverter : JsonConverter<SlackResponse>
    {
        public override SlackResponse? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException();

            var originalReader = reader;

            bool? ok = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string propertyName = reader.GetString() ?? throw new JsonException();
                    if (propertyName == "ok")
                    {
                        reader.Read();
                        ok = reader.GetBoolean();
                        break;
                    }
                }
            }

            if (ok == null)
                throw new JsonException();

            SlackResponse? result = ok.Value
                ? JsonSerializer.Deserialize<SlackOkResponse>(ref originalReader, options)
                : JsonSerializer.Deserialize<SlackErrorResponse>(ref originalReader, options);

            reader = originalReader;

            return result;
        }

        public override void Write(Utf8JsonWriter writer, SlackResponse value, JsonSerializerOptions options)
        {
            if (value is SlackOkResponse ok)
                JsonSerializer.Serialize(writer, ok, options);
            else if (value is SlackErrorResponse error)
                JsonSerializer.Serialize(writer, error, options);
            else
                throw new JsonException();
        }
    }
}
