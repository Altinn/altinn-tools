using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Altinn.Apps.Monitoring.Application;
using Altinn.Apps.Monitoring.Application.Db;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using NodaTime.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ConfigureHostOptions(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(20));

if (!builder.IsLocal())
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.All;
        options.KnownNetworks.Clear(); // Loopback by default, we don't have a stable known network
        options.KnownProxies.Clear();
    });
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.AddApplication();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

if (!builder.IsLocal())
    app.UseForwardedHeaders();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet(
        "/health/ready",
        Results<Utf8ContentHttpResult, InternalServerError> () =>
        {
            // Check dependencies?
            return TypedResults.Text("Healthy"u8, contentType: "text/plain", statusCode: 200);
        }
    )
    .WithOpenApi(operation =>
    {
        operation.Tags.Clear();
        operation.Tags.Add(new OpenApiTag { Name = "Operational" });
        operation.OperationId = "Health / Ready";
        return operation;
    });

app.MapGet(
        "/health/live",
        Results<Utf8ContentHttpResult, InternalServerError> () =>
        {
            // Check dependencies?
            return TypedResults.Text("Healthy"u8, contentType: "text/plain", statusCode: 200);
        }
    )
    .WithOpenApi(operation =>
    {
        operation.Tags.Clear();
        operation.Tags.Add(new OpenApiTag { Name = "Operational" });
        operation.OperationId = "Health / Live";
        return operation;
    });

app.MapPost(
        "/query/metrics/{from}/{to}",
        async ValueTask<Results<Ok<MetricsQueryResponse>, BadRequest<ProblemDetails>, InternalServerError>> (
            HttpRequest request,
            [FromRoute] DateTimeOffset from,
            [FromRoute] DateTimeOffset to,
            CancellationToken cancellationToken
        ) =>
        {
            if (request.ContentType != "text/plain")
            {
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Type = "invalid-content-type",
                        Title = "Invalid content type",
                        Detail = "Only text/plain content type is supported",
                        Status = StatusCodes.Status415UnsupportedMediaType,
                    }
                );
            }

            if (to - from > TimeSpan.FromDays(60))
            {
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Type = "invalid-time-range",
                        Title = "Invalid time range",
                        Detail = "Time range must be less than 60 days",
                        Status = StatusCodes.Status400BadRequest,
                    }
                );
            }

            var serviceOwnerDiscovery =
                request.HttpContext.RequestServices.GetRequiredService<IServiceOwnerDiscovery>();
            var serviceOwners = await serviceOwnerDiscovery.Discover(cancellationToken);
            if (serviceOwners.Count == 0)
                return TypedResults.Ok(new MetricsQueryResponse([]));

            string queryTemplate;
            using (var reader = new StreamReader(request.Body))
            {
                queryTemplate = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(queryTemplate))
                {
                    return TypedResults.BadRequest(
                        new ProblemDetails
                        {
                            Type = "empty-query-template",
                            Title = "Empty query-template",
                            Detail = "No query template provided",
                            Status = StatusCodes.Status400BadRequest,
                        }
                    );
                }
            }
            var query = new Query("Input", QueryType.Metrics, queryTemplate);
            var adapter = request.HttpContext.RequestServices.GetRequiredService<IServiceOwnerMetricsAdapter>();
            ConcurrentBag<IReadOnlyList<MetricItem>> metrics = new();
            await Parallel.ForEachAsync(
                serviceOwners,
                new ParallelOptions { CancellationToken = cancellationToken },
                async (serviceOwner, cancellationToken) =>
                {
                    var serviceOwnerMetrics = await adapter.Query(
                        serviceOwner,
                        query,
                        from.ToInstant(),
                        to.ToInstant(),
                        cancellationToken
                    );

                    metrics.Add(
                        serviceOwnerMetrics
                            .SelectMany(x =>
                                x.Select(t =>
                                {
                                    var metric = (MetricData)t.Data;
                                    return new MetricItem(
                                        serviceOwner.Value,
                                        t.TimeGenerated,
                                        t.AppName,
                                        metric.Name,
                                        metric.Value
                                    );
                                })
                            )
                            .ToArray()
                    );
                }
            );

            var items = metrics.SelectMany(x => x).ToArray();
            return TypedResults.Ok(new MetricsQueryResponse(items));
        }
    )
    .WithOpenApi(operation =>
    {
        operation.Tags.Clear();
        operation.Tags.Add(new OpenApiTag { Name = "Query" });
        operation.OperationId = "Metrics";

        var fromParameter = operation.Parameters.Single(p => p.Name == "from");
        fromParameter.Example = new OpenApiString("2025-03-01T00:00:00Z");

        var toParameter = operation.Parameters.Single(p => p.Name == "to");
        toParameter.Example = new OpenApiString("2025-03-04T10:00:00Z");

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                ["text/plain"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "string",
                        Example = new OpenApiString(
                            """
                            AppRequests
                            | where TimeGenerated >= datetime('{0}') and TimeGenerated < datetime('{1}')
                            | where Name == 'GET Authorization/GetRolesForCurrentParty [app/org]'
                            | summarize ['Value'] = sum(ItemCount) by bin(TimeGenerated, 1d), App = AppRoleName, AppVersion, Name
                            | order by TimeGenerated desc
                            """
                        ),
                        Nullable = false,
                    },
                },
            },
        };
        return operation;
    });

app.Run();

sealed record MetricsQueryResponse(IReadOnlyList<MetricItem> Items);

sealed record MetricItem(string ServiceOwner, Instant TimeGenerated, string App, string Name, double Value);
