namespace self_API_Gateway.Configuration;

// Plain POCOs with public setters: bound by the compile-time configuration binding generator (AOT-safe).
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public List<RouteConfig> Routes { get; set; } = [];
    public Dictionary<string, ClusterConfig> Clusters { get; set; } = [];
    public Dictionary<string, RateLimitPolicyConfig> RateLimitPolicies { get; set; } = [];

    // Proxies in front of the gateway whose X-Forwarded-* headers are trusted (loopback is always trusted).
    public List<string> KnownProxies { get; set; } = [];
    // Same, as CIDR ranges, e.g. "172.16.0.0/12" for a Docker network.
    public List<string> KnownNetworks { get; set; } = [];
}

public sealed class RouteConfig
{
    public string Id { get; set; } = "";
    public string PathPrefix { get; set; } = "";
    public string ClusterId { get; set; } = "";
    public string? RateLimitPolicy { get; set; }
}

public sealed class ClusterConfig
{
    public string Destination { get; set; } = "";
    public HealthCheckConfig? HealthCheck { get; set; }
}

public sealed class HealthCheckConfig
{
    public string Path { get; set; } = "/health";
    public int IntervalSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 2;
}

public sealed class RateLimitPolicyConfig
{
    // FixedWindow | SlidingWindow | TokenBucket
    public string Algorithm { get; set; } = "TokenBucket";
    // Ip | ApiKey | User
    public string PartitionBy { get; set; } = "Ip";
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    // Window size for the window algorithms, bucket size for TokenBucket.
    public int PermitLimit { get; set; } = 100;
    // Window length, or the replenishment period for TokenBucket.
    public double WindowSeconds { get; set; } = 1;
    public int TokensPerPeriod { get; set; } = 50;
    public int SegmentsPerWindow { get; set; } = 4;
    public int QueueLimit { get; set; }
}
