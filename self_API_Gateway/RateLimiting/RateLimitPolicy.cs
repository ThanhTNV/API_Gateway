using System.Globalization;
using System.Threading.RateLimiting;
using self_API_Gateway.Configuration;

namespace self_API_Gateway.RateLimiting;

// A validated rate limit policy, attached to route endpoints as metadata.
public sealed class RateLimitPolicy
{
    private enum Algorithm { FixedWindow, SlidingWindow, TokenBucket }
    private enum PartitionBy { Ip, ApiKey, User }

    private readonly Algorithm _algorithm;
    private readonly PartitionBy _partitionBy;
    private readonly string _keyPrefix;

    public RateLimitPolicy(string name, RateLimitPolicyConfig config)
    {
        if (config is null)
            throw new GatewayConfigException($"Rate limit policy '{name}' is empty.");

        _algorithm = config.Algorithm?.ToLowerInvariant() switch
        {
            "fixedwindow" => Algorithm.FixedWindow,
            "slidingwindow" => Algorithm.SlidingWindow,
            "tokenbucket" => Algorithm.TokenBucket,
            _ => throw new GatewayConfigException(
                $"Rate limit policy '{name}': unknown Algorithm '{config.Algorithm}'. Expected FixedWindow, SlidingWindow or TokenBucket.")
        };
        _partitionBy = config.PartitionBy?.ToLowerInvariant() switch
        {
            "ip" => PartitionBy.Ip,
            "apikey" => PartitionBy.ApiKey,
            "user" => PartitionBy.User,
            _ => throw new GatewayConfigException(
                $"Rate limit policy '{name}': unknown PartitionBy '{config.PartitionBy}'. Expected Ip, ApiKey or User.")
        };

        // Checked here because the limiter would otherwise only throw on the first request.
        if (config.PermitLimit <= 0)
            throw new GatewayConfigException($"Rate limit policy '{name}': PermitLimit must be positive.");
        if (config.WindowSeconds is not (> 0 and <= 86400))
            throw new GatewayConfigException($"Rate limit policy '{name}': WindowSeconds must be between 0 and 86400.");
        if (config.QueueLimit < 0)
            throw new GatewayConfigException($"Rate limit policy '{name}': QueueLimit must not be negative.");
        if (_algorithm == Algorithm.TokenBucket && config.TokensPerPeriod <= 0)
            throw new GatewayConfigException($"Rate limit policy '{name}': TokensPerPeriod must be positive.");
        if (_algorithm == Algorithm.SlidingWindow && config.SegmentsPerWindow <= 0)
            throw new GatewayConfigException($"Rate limit policy '{name}': SegmentsPerWindow must be positive.");
        if (_partitionBy == PartitionBy.ApiKey && string.IsNullOrEmpty(config.ApiKeyHeader))
            throw new GatewayConfigException($"Rate limit policy '{name}': ApiKeyHeader is required when PartitionBy is ApiKey.");

        Name = name;
        Config = config;

        // Limiters are cached per partition key. Putting the settings in the key gives a changed
        // policy fresh limiters, while unchanged policies keep their counters across a config change.
        _keyPrefix = string.Create(CultureInfo.InvariantCulture,
            $"{name}|{_algorithm}|{_partitionBy}|{config.PermitLimit}|{config.TokensPerPeriod}|{config.WindowSeconds}|{config.SegmentsPerWindow}|{config.QueueLimit}|");
    }

    public string Name { get; }
    public RateLimitPolicyConfig Config { get; }

    public RateLimitPartition<string> GetPartition(HttpContext ctx)
    {
        var key = _keyPrefix + GetClientKey(ctx);
        var window = TimeSpan.FromSeconds(Config.WindowSeconds);
        return _algorithm switch
        {
            Algorithm.FixedWindow => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Config.PermitLimit,
                Window = window,
                QueueLimit = Config.QueueLimit
            }),
            Algorithm.SlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Config.PermitLimit,
                Window = window,
                SegmentsPerWindow = Config.SegmentsPerWindow,
                QueueLimit = Config.QueueLimit
            }),
            _ => RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = Config.PermitLimit,
                TokensPerPeriod = Config.TokensPerPeriod,
                ReplenishmentPeriod = window,
                QueueLimit = Config.QueueLimit
            })
        };
    }

    // Callers without an API key or identity fall back to their IP; the prefix keeps the key spaces apart.
    private string GetClientKey(HttpContext ctx)
    {
        if (_partitionBy == PartitionBy.ApiKey && ctx.Request.Headers[Config.ApiKeyHeader].ToString() is { Length: > 0 } apiKey)
        {
            return "key:" + apiKey;
        }
        if (_partitionBy == PartitionBy.User && ctx.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } user })
        {
            return "user:" + user;
        }
        return "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
