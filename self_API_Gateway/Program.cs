using self_API_Gateway.Admin;
using self_API_Gateway.Health;
using self_API_Gateway.Logging;
using self_API_Gateway.Proxy;
using self_API_Gateway.RateLimiting;
using self_API_Gateway.Routing;

var builder = WebApplication.CreateSlimBuilder(args);

// Daily log file (yyyy-MM-dd.log) alongside the console; relative paths are resolved against the content root.
builder.Logging.AddDailyFile(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["Logging:File:Directory"] ?? "logs"));

builder.Services.AddSingleton(_ => HttpForwarder.CreateInvoker());
builder.Services.AddSingleton<HttpForwarder>();
// Loads the Gateway configuration (the change log if present, appsettings.json otherwise) and owns it from then on.
builder.Services.AddSingleton<GatewayRuntime>();
builder.Services.AddHostedService<HealthCheckService>();
builder.Services.AddGatewayRateLimiting();
builder.Services.AddGatewayForwardedHeaders();

var app = builder.Build();

// Must run before the rate limiter so per-IP partitions see the real client address.
app.UseForwardedHeaders();
app.UseRateLimiter();

app.MapGateway();
app.MapGatewayAdmin();

app.Run();
