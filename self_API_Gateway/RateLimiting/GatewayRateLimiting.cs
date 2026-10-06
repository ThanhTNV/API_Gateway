using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using self_API_Gateway.Routing;

namespace self_API_Gateway.RateLimiting;

public static class GatewayRateLimiting
{
    // The one policy registered with the middleware. It defers to the RateLimitPolicy in the matched
    // route's metadata, which is what lets policies change at runtime.
    public const string PolicyName = "gateway";

    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.AddPolicy(PolicyName, ctx => ctx.GetEndpoint()?.Metadata.GetMetadata<RateLimitPolicy>() is { } policy
                ? policy.GetPartition(ctx)
                : RateLimitPartition.GetNoLimiter(""));

            o.OnRejected = (context, _) =>
            {
                var http = context.HttpContext;
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<RateLimitPolicy>();

                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(GatewayRateLimiting).FullName!)
                    .LogWarning("Rate limit policy {Policy} rejected {Method} {Path} from {ClientIp} with 429",
                        policy?.Name, http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress);

                // Sliding window leases carry no retry hint: fall back to the policy's window.
                if (!context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && policy is not null)
                {
                    retryAfter = TimeSpan.FromSeconds(policy.Config.WindowSeconds);
                }

                if (retryAfter > TimeSpan.Zero)
                {
                    http.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };
        });

    // Behind another proxy RemoteIpAddress is the proxy's address, so every client would share one bucket.
    // Trust X-Forwarded-* from the configured proxies to recover the real client address.
    // Read once at startup: a change through the admin API takes effect on the next restart.
    public static IServiceCollection AddGatewayForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<GatewayRuntime>((o, runtime) =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in runtime.StartupOptions.KnownProxies)
            {
                o.KnownProxies.Add(IPAddress.Parse(proxy));
            }
            foreach (var network in runtime.StartupOptions.KnownNetworks)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        return services;
    }
}
