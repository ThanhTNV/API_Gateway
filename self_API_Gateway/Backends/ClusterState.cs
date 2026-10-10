using self_API_Gateway.Configuration;

namespace self_API_Gateway.Backends;

public sealed class DestinationState(Uri address)
{
    private volatile bool _isHealthy = true;

    public Uri Address { get; } = address;

    // Address without a trailing slash, ready to have a path appended.
    public string BaseUrl { get; } = address.GetLeftPart(UriPartial.Path).TrimEnd('/');

    // Starts out healthy so traffic flows before the first probe completes.
    public bool IsHealthy
    {
        get => _isHealthy;
        set => _isHealthy = value;
    }
}

public sealed class ClusterState(
    string id,
    DestinationState destinations,
    HealthCheckConfig? healthCheck)
{
    public string Id { get; } = id;
    public DestinationState Destination { get; } = destinations;
    public HealthCheckConfig? HealthCheck { get; } = healthCheck;

    public DestinationState? GetHealthyDestination() =>
        Destination.IsHealthy ? Destination : null;

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
            if (config?.Destination is null || String.IsNullOrWhiteSpace(config.Destination))
                throw new GatewayConfigException($"Cluster '{id}' has no destination.");
            if (config.HealthCheck is { } healthCheck && healthCheck.Path?.StartsWith('/') != true)
                throw new GatewayConfigException($"Cluster '{id}': HealthCheck.Path must start with '/'.");

            // Without a health check nothing would revive an unhealthy destination, so those start fresh.
            ClusterState? previousCluster = null;
            if (config.HealthCheck is not null)
            {
                previous?.TryGet(id, out previousCluster);
            }

            if (!Uri.TryCreate(config.Destination, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new GatewayConfigException(
                    $"Cluster '{id}' has an invalid destination '{config.Destination}'. Expected an absolute http(s) URL.");
            }
            var destination =
                previousCluster?.Destination.Address == uri
                    ? previousCluster.Destination
                    : new DestinationState(uri);
            _clusters[id] = new ClusterState(id, destination, config.HealthCheck);
        }
    }

    public IReadOnlyCollection<ClusterState> Clusters => _clusters.Values;

    public bool TryGet(string id, out ClusterState cluster) => _clusters.TryGetValue(id, out cluster!);
}
