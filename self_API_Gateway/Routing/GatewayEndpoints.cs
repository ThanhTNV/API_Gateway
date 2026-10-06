using self_API_Gateway.Configuration;
using self_API_Gateway.LoadBalancing;
using self_API_Gateway.Proxy;

namespace self_API_Gateway.Routing;

public static class GatewayEndpoints
{
    public static void MapGatewayRoutes(this WebApplication app, GatewayOptions options)
    {
        var registry = app.Services.GetRequiredService<ClusterRegistry>();
        var forwarder = app.Services.GetRequiredService<HttpForwarder>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GatewayEndpoints).FullName!);

        foreach (var route in options.Routes)
        {
            if (!route.PathPrefix.StartsWith('/'))
                throw new InvalidOperationException($"Route '{route.Id}': PathPrefix '{route.PathPrefix}' must start with '/'.");
            if (!registry.TryGet(route.ClusterId, out var cluster))
                throw new InvalidOperationException($"Route '{route.Id}' references unknown cluster '{route.ClusterId}'.");

            var prefix = new PathString(route.PathPrefix.TrimEnd('/'));

            RequestDelegate handler = ctx =>
            {
                var destination = cluster.PickDestination();
                if (destination is null)
                {
                    logger.LogWarning("No healthy destination in cluster {Cluster} for route {Route}: {Method} {Path} rejected with 503",
                        cluster.Id, route.Id, ctx.Request.Method, ctx.Request.Path);
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.CompletedTask;
                }
                return forwarder.ForwardAsync(ctx, cluster, destination, prefix);
            };

            // The catch-all also matches the bare prefix.
            var endpoint = app.Map(prefix + "/{**rest}", handler).WithDisplayName(route.Id);

            if (!string.IsNullOrEmpty(route.RateLimitPolicy))
            {
                if (!options.RateLimitPolicies.ContainsKey(route.RateLimitPolicy))
                    throw new InvalidOperationException($"Route '{route.Id}' references unknown rate limit policy '{route.RateLimitPolicy}'.");
                endpoint.RequireRateLimiting(route.RateLimitPolicy);
            }
        }
    }
}
