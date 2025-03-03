using Altinn.Apps.Monitoring.Application;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

if (!builder.IsLocal())
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.All;
        options.KnownNetworks.Clear(); // Loopback by default, we don't have a stable known network
        options.KnownProxies.Clear();
    });
}

builder.AddApplication();

var app = builder.Build();

if (!builder.IsLocal())
    app.UseForwardedHeaders();

app.MapOpenApi();

app.MapGet(
        "/health/ready",
        Results<Utf8ContentHttpResult, InternalServerError> () =>
        {
            // Check dependencies?
            return TypedResults.Text("Healthy"u8, contentType: "text/plain", statusCode: 200);
        }
    )
    .WithName("Health / Ready");

app.MapGet(
        "/health/live",
        Results<Utf8ContentHttpResult, InternalServerError> () =>
        {
            // Check dependencies?
            return TypedResults.Text("Healthy"u8, contentType: "text/plain", statusCode: 200);
        }
    )
    .WithName("Startup / Live");

app.Run();
