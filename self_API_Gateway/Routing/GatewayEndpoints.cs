using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing.Patterns;
using self_API_Gateway.Configuration;
using self_API_Gateway.LoadBalancing;
using self_API_Gateway.Proxy;
using self_API_Gateway.RateLimiting;

namespace self_API_Gateway.Routing;

public static class GatewayEndpoints
{
    // Routes come from a data source rather than app.Map so they can be replaced at runtime.
    public static void MapGateway(this IEndpointRouteBuilder endpoints) =>
        endpoints.DataSources.Add(endpoints.ServiceProvider.GetRequiredService<GatewayRuntime>().EndpointDataSource);

    // Builds one endpoint per route. Throws GatewayConfigException without side effects when the configuration is invalid.
    internal static List<Endpoint> Build(GatewayOptions options, ClusterRegistry registry, HttpForwarder forwarder, ILogger logger)
    {
        var policies = new Dictionary<string, RateLimitPolicy>();
        foreach (var (name, config) in options.RateLimitPolicies)
        {
            policies[name] = new RateLimitPolicy(name, config);
        }

        var endpoints = new List<Endpoint>(options.Routes.Count);
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in options.Routes)
        {
            if (route is null)
                throw new GatewayConfigException("Routes must not contain null entries.");
            if (string.IsNullOrEmpty(route.PathPrefix) || route.PathPrefix[0] != '/' || route.PathPrefix.AsSpan().IndexOfAny("{}*?") >= 0)
                throw new GatewayConfigException($"Route '{route.Id}': PathPrefix '{route.PathPrefix}' must start with '/' and must not contain any of {{ }} * ?.");
            if (route.ClusterId is null || !registry.TryGet(route.ClusterId, out var cluster))
                throw new GatewayConfigException($"Route '{route.Id}' references unknown cluster '{route.ClusterId}'.");

            var trimmed = route.PathPrefix.TrimEnd('/');
            if (!prefixes.Add(trimmed))
                throw new GatewayConfigException($"Route '{route.Id}': PathPrefix '{route.PathPrefix}' is already used by another route.");

            RateLimitPolicy? policy = null;
            if (!string.IsNullOrEmpty(route.RateLimitPolicy) && !policies.TryGetValue(route.RateLimitPolicy, out policy))
                throw new GatewayConfigException($"Route '{route.Id}' references unknown rate limit policy '{route.RateLimitPolicy}'.");

            var prefix = new PathString(trimmed);
            var routeId = route.Id;

            RequestDelegate handler = ctx =>
            {
                var destination = cluster.PickDestination();
                if (destination is null)
                {
                    logger.LogWarning("No healthy destination in cluster {Cluster} for route {Route}: {Method} {Path} rejected with 503",
                        cluster.Id, routeId, ctx.Request.Method, ctx.Request.Path);
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.CompletedTask;
                }
                return forwarder.ForwardAsync(ctx, cluster, destination, prefix);
            };

            RoutePattern pattern;
            try
            {
                // The catch-all also matches the bare prefix.
                pattern = RoutePatternFactory.Parse(trimmed + "/{**rest}");
            }
            catch (RoutePatternException ex)
            {
                throw new GatewayConfigException($"Route '{route.Id}': PathPrefix '{route.PathPrefix}' is not a valid path. {ex.Message}");
            }

            var builder = new RouteEndpointBuilder(handler, pattern, order: 0) { DisplayName = routeId };
            if (policy is not null)
            {
                builder.Metadata.Add(new EnableRateLimitingAttribute(GatewayRateLimiting.PolicyName));
                builder.Metadata.Add(policy);
            }
            endpoints.Add(builder.Build());
        }

        return endpoints;
    }
}
