using self_API_Gateway.Configuration;

namespace self_API_Gateway.LoadBalancing;

public sealed class DestinationState(Uri address)
{
    private volatile bool _isHealthy = true;
    private int _activeRequests;

    public Uri Address { get; } = address;

    // Address without a trailing slash, ready to have a path appended.
    public string BaseUrl { get; } = address.GetLeftPart(UriPartial.Path).TrimEnd('/');

    // Starts out healthy so traffic flows before the first probe completes.
    public bool IsHealthy
    {
        get => _isHealthy;
        set => _isHealthy = value;
    }

    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    public void BeginRequest() => Interlocked.Increment(ref _activeRequests);
    public void EndRequest() => Interlocked.Decrement(ref _activeRequests);
}

public sealed class ClusterState(
    string id,
    IReadOnlyList<DestinationState> destinations,
    ILoadBalancer loadBalancer,
    HealthCheckConfig? healthCheck)
{
    public string Id { get; } = id;
    public IReadOnlyList<DestinationState> Destinations { get; } = destinations;
    public ILoadBalancer LoadBalancer { get; } = loadBalancer;
    public HealthCheckConfig? HealthCheck { get; } = healthCheck;

    public DestinationState? PickDestination()
    {
        var healthy = new List<DestinationState>(Destinations.Count);
        foreach (var destination in Destinations)
        {
            if (destination.IsHealthy) healthy.Add(destination);
        }
        return LoadBalancer.Pick(healthy);
    }

    // Passive health: take a destination out of rotation as soon as forwarding to it fails.
    // Only done when an active health check exists to bring it back.
    public bool ReportFailure(DestinationState destination)
    {
        if (HealthCheck is null) return false;
        destination.IsHealthy = false;
        return true;
    }
}

public sealed class ClusterRegistry
{
    private readonly Dictionary<string, ClusterState> _clusters = [];

    // Pass the registry being replaced to carry health state over for destinations that stay.
    public ClusterRegistry(GatewayOptions options, ClusterRegistry? previous = null)
    {
        foreach (var (id, config) in options.Clusters)
        {
            if (config?.Destinations is null or { Count: 0 })
                throw new GatewayConfigException($"Cluster '{id}' has no destinations.");
            if (config.HealthCheck is { } healthCheck && healthCheck.Path?.StartsWith('/') != true)
                throw new GatewayConfigException($"Cluster '{id}': HealthCheck.Path must start with '/'.");

            // Without a health check nothing would revive an unhealthy destination, so those start fresh.
            ClusterState? previousCluster = null;
            if (config.HealthCheck is not null)
            {
                previous?.TryGet(id, out previousCluster);
            }

            var destinations = new DestinationState[config.Destinations.Count];
            for (var i = 0; i < destinations.Length; i++)
            {
                var address = config.Destinations[i];
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new GatewayConfigException(
                        $"Cluster '{id}' has an invalid destination '{address}'. Expected an absolute http(s) URL.");
                }
                destinations[i] = previousCluster?.Destinations.FirstOrDefault(d => d.Address == uri) ?? new DestinationState(uri);
            }

            _clusters[id] = new ClusterState(id, destinations, LoadBalancerFactory.Create(config.LoadBalancing), config.HealthCheck);
        }
    }

    public IReadOnlyCollection<ClusterState> Clusters => _clusters.Values;

    public bool TryGet(string id, out ClusterState cluster) => _clusters.TryGetValue(id, out cluster!);
}
