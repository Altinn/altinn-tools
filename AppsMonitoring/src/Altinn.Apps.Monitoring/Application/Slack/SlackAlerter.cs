using System.Diagnostics;
using System.Net;
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
    Repository repository,
    Telemetry telemetry
) : IAlerter, IDisposable
{
    // NOTE: if retry behavior is changed, also update the expected retry behavior in the SlackAlerterTests
    internal const int MaxSlackApiRetryAttempts = 3;
    private readonly HttpClient _httpClient = new HttpClient(
        new ResilienceHandler(
            new ResiliencePipelineBuilder<HttpResponseMessage>() { TimeProvider = timeProvider }
                .AddRetry(
                    new HttpRetryStrategyOptions
                    {
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = MaxSlackApiRetryAttempts,
                    }
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
    private readonly Repository _repository = repository;
#pragma warning disable CA2213 // Disposable fields should be disposed
    // DI container owns telemetry
    private readonly Telemetry _telemetry = telemetry;
#pragma warning restore CA2213 // Disposable fields should be disposed

    private Channel<AlerterEvent>? _events;

    public ChannelReader<AlerterEvent> Events => _events?.Reader ?? throw new InvalidOperationException("Not started");

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _thread;

    public Task Start(CancellationToken cancellationToken)
    {
        using var activity = _telemetry.Activities.StartActivity("SlackAlerter.Start");
        _events = Channel.CreateBounded<AlerterEvent>(
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
        var startActivity = _telemetry.Activities.StartRootActivity("SlackAlerter.Thread.Start");
        var config = _config.CurrentValue;
        try
        {
            using var timer = new PeriodicTimer(config.PollInterval / 2, _timeProvider);

            startActivity?.Dispose();
            startActivity = null;
            do
            {
                using var iterationActivity = _telemetry.Activities.StartRootActivity("SlackAlerter.Thread.Iteration");

                var workItems = await _repository.ListAlerterWorkItems(AlertData.Types.Slack, cancellationToken);
                if (workItems.Length == 0)
                    continue;

                Debug.Assert(_events is not null);

                for (int i = 0; i < workItems.Length; i++)
                {
                    var (item, alert) = workItems[i];
                    using var activity = _telemetry.Activities.StartActivity("SlackAlerter.Thread.WorkItem");
                    activity?.SetTag("telemetry.id", item.Id);
                    activity?.SetTag("telemetry.extid", item.ExtId);
                    activity?.SetTag("serviceowner", item.ServiceOwner);
                    activity?.SetTag("alert.id", alert?.Id ?? -1);
                    activity?.SetTag("alert.state", alert?.State.ToString() ?? "");
                    try
                    {
                        if (alert is null)
                        {
                            var now = _timeProvider.GetCurrentInstant();
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
                                CreatedAt = now,
                                UpdatedAt = now,
                            };
                            workItems[i] = (item, alert);
                        }

                        while (alert.State < AlertState.Mitigated)
                        {
                            var updatedAlert = await ProgressAlert(item, alert, cancellationToken);
                            var alertEvent = new AlerterEvent
                            {
                                Item = item,
                                AlertBefore = alert,
                                AlertAfter = updatedAlert,
                            };
                            await _events.Writer.WriteAsync(alertEvent, cancellationToken);
                            if (updatedAlert is null)
                                break; // Try again later

                            await _repository.SaveAlert(updatedAlert, cancellationToken);

                            alert = updatedAlert;
                            workItems[i] = (item, alert);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        activity?.AddException(ex);
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
            startActivity?.AddException(ex);
            _logger.LogError(ex, "Alerter thread failed");
            _lifetime.StopApplication();
        }
        finally
        {
            startActivity?.Dispose();
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
                var config = _config.CurrentValue;
                try
                {
                    var channel = config.SlackChannel;
                    if (config.DisableSlackAlerts)
                    {
                        _logger.LogInformation("Would have sent alert to Slack for: {TelemetyrId}", alert.TelemetryId);
                        return alert with
                        {
                            State = AlertState.Alerted,
                            Data = alertData with { Message = text, Channel = channel, ThreadTs = "none" },
                            UpdatedAt = _timeProvider.GetCurrentInstant(),
                        };
                    }
                    using var response = await _httpClient.PostAsJsonAsync(
                        "/api/chat.postMessage",
                        new
                        {
                            channel = channel,
                            text = text,
                            mrkdwn = true,
                            // thread_ts - we will use this to update the Slack alert when mitigations have been made (not here though)
                        },
                        cancellationToken
                    );

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogWarning("Slack rate limit exceeded, will try again later");
                        return null;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError(
                            "Failed to send alert to Slack: {StatusCode} {Reason}",
                            response.StatusCode,
                            response.ReasonPhrase
                        );
                        return null;
                    }

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
                            UpdatedAt = _timeProvider.GetCurrentInstant(),
                        };
                    }
                    else
                        throw new InvalidOperationException($"Unknown Slack response: {slackResponse}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to send alert to Slack");
                    return null;
                }
            }
            default:
                throw new InvalidOperationException($"Unknown telemetry data: {item.Data}");
        }
    }

    public async Task Stop()
    {
        if (_thread is not null)
            await _thread;

        if (_events is not null)
            _events.Writer.TryComplete();
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
        _httpClient.Dispose();
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
