using self_API_Gateway.Configuration;
using self_API_Gateway.Health;
using self_API_Gateway.LoadBalancing;
using self_API_Gateway.Logging;
using self_API_Gateway.Proxy;
using self_API_Gateway.RateLimiting;
using self_API_Gateway.Routing;

var builder = WebApplication.CreateSlimBuilder(args);

// Daily log file (yyyy-MM-dd.log) alongside the console; relative paths are resolved against the content root.
builder.Logging.AddDailyFile(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["Logging:File:Directory"] ?? "logs"));

var gateway =builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();

builder.Services.AddSingleton(gateway);
builder.Services.AddSingleton<ClusterRegistry>();
builder.Services.AddSingleton(_ => HttpForwarder.CreateInvoker());
builder.Services.AddSingleton<HttpForwarder>();
builder.Services.AddHostedService<HealthCheckService>();
builder.Services.AddGatewayRateLimiting(gateway);
builder.Services.AddGatewayForwardedHeaders(gateway);

var app = builder.Build();

// Must run before the rate limiter so per-IP partitions see the real client address.
app.UseForwardedHeaders();
app.UseRateLimiter();

app.MapGatewayRoutes(gateway);

app.Run();
