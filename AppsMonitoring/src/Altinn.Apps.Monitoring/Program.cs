using Altinn.Apps.Monitoring.Application;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

if (!builder.Environment.IsDevelopment())
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

if (!builder.Environment.IsDevelopment())
    app.UseForwardedHeaders();

app.MapOpenApi();

app.MapGet(
        "/health",
        Results<Utf8ContentHttpResult, InternalServerError> () =>
        {
            // Check dependencies?
            return TypedResults.Text("Healthy"u8, contentType: "text/plain", statusCode: 200);
        }
    )
    .WithName("Health");

app.Run();
