using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using self_API_Gateway.Configuration;

namespace self_API_Gateway.RateLimiting;

public static class GatewayRateLimiting
{
    private enum Algorithm { FixedWindow, SlidingWindow, TokenBucket }
    private enum PartitionBy { Ip, ApiKey, User }

    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services, GatewayOptions gateway)
    {
        // Parsed up front so a typo in config fails at startup, not on the first request.
        var policies = new List<(string Name, RateLimitPolicyConfig Config, Algorithm Algorithm, PartitionBy PartitionBy)>();
        foreach (var (name, config) in gateway.RateLimitPolicies)
        {
            if (config.PermitLimit <= 0 || config.WindowSeconds <= 0)
                throw new InvalidOperationException($"Rate limit policy '{name}': PermitLimit and WindowSeconds must be positive.");
            policies.Add((name, config, ParseAlgorithm(name, config.Algorithm), ParsePartitionBy(name, config.PartitionBy)));
        }

        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            foreach (var (name, config, algorithm, partitionBy) in policies)
            {
                o.AddPolicy(name, ctx => CreatePartition(GetPartitionKey(ctx, config, partitionBy), config, algorithm));
            }

            o.OnRejected = (context, _) =>
            {
                var http = context.HttpContext;
                var policyName = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(GatewayRateLimiting).FullName!)
                    .LogWarning("Rate limit policy {Policy} rejected {Method} {Path} from {ClientIp} with 429",
                        policyName, http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress);

                var retryAfter = TimeSpan.Zero;
                if (!context.Lease.TryGetMetadata(MetadataName.RetryAfter, out retryAfter))
                {
                    // Sliding window leases carry no retry hint: fall back to the policy's window.
                    if (policyName is not null && gateway.RateLimitPolicies.TryGetValue(policyName, out var config))
                    {
                        retryAfter = TimeSpan.FromSeconds(config.WindowSeconds);
                    }
                }

                if (retryAfter > TimeSpan.Zero)
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };
        });
    }

    // Behind another proxy RemoteIpAddress is the proxy's address, so every client would share one bucket.
    // Trust X-Forwarded-* from the configured proxies to recover the real client address.
    public static IServiceCollection AddGatewayForwardedHeaders(this IServiceCollection services, GatewayOptions gateway) =>
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in gateway.KnownProxies)
            {
                o.KnownProxies.Add(IPAddress.Parse(proxy));
            }
            foreach (var network in gateway.KnownNetworks)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

    private static RateLimitPartition<string> CreatePartition(string key, RateLimitPolicyConfig config, Algorithm algorithm)
    {
        var window = TimeSpan.FromSeconds(config.WindowSeconds);
        return algorithm switch
        {
            Algorithm.FixedWindow => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.PermitLimit,
                Window = window,
                QueueLimit = config.QueueLimit
            }),
            Algorithm.SlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = config.PermitLimit,
                Window = window,
                SegmentsPerWindow = config.SegmentsPerWindow,
                QueueLimit = config.QueueLimit
            }),
            _ => RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = config.PermitLimit,
                TokensPerPeriod = config.TokensPerPeriod,
                ReplenishmentPeriod = window,
                QueueLimit = config.QueueLimit
            })
        };
    }

    // Callers without an API key or identity fall back to their IP; the prefix keeps the key spaces apart.
    private static string GetPartitionKey(HttpContext ctx, RateLimitPolicyConfig config, PartitionBy partitionBy)
    {
        if (partitionBy == PartitionBy.ApiKey && ctx.Request.Headers[config.ApiKeyHeader].ToString() is { Length: > 0 } apiKey)
        {
            return "key:" + apiKey;
        }
        if (partitionBy == PartitionBy.User && ctx.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } user })
        {
            return "user:" + user;
        }
        return "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static Algorithm ParseAlgorithm(string policy, string value) => value.ToLowerInvariant() switch
    {
        "fixedwindow" => Algorithm.FixedWindow,
        "slidingwindow" => Algorithm.SlidingWindow,
        "tokenbucket" => Algorithm.TokenBucket,
        _ => throw new InvalidOperationException(
            $"Rate limit policy '{policy}': unknown Algorithm '{value}'. Expected FixedWindow, SlidingWindow or TokenBucket.")
    };

    private static PartitionBy ParsePartitionBy(string policy, string value) => value.ToLowerInvariant() switch
    {
        "ip" => PartitionBy.Ip,
        "apikey" => PartitionBy.ApiKey,
        "user" => PartitionBy.User,
        _ => throw new InvalidOperationException(
            $"Rate limit policy '{policy}': unknown PartitionBy '{value}'. Expected Ip, ApiKey or User.")
    };
}
