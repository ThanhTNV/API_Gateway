using self_API_Gateway.LoadBalancing;

namespace self_API_Gateway.Health;

public sealed class HealthCheckService(
    ClusterRegistry registry,
    HttpMessageInvoker invoker,
    ILogger<HealthCheckService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(registry.Clusters
            .Where(c => c.HealthCheck is not null)
            .Select(c => RunClusterAsync(c, stoppingToken)));

    private async Task RunClusterAsync(ClusterState cluster, CancellationToken stoppingToken)
    {
        var config = cluster.HealthCheck!;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, config.IntervalSeconds)));

        try
        {
            do
            {
                await Task.WhenAll(cluster.Destinations.Select(d => ProbeAsync(cluster, d, config.Path, timeout, stoppingToken)));
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeAsync(ClusterState cluster, DestinationState destination, string path, TimeSpan timeout, CancellationToken stoppingToken)
    {
        bool healthy;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, destination.BaseUrl + path);
            using var response = await invoker.SendAsync(request, cts.Token);
            healthy = response.IsSuccessStatusCode;
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            healthy = false;
        }

        if (destination.IsHealthy != healthy)
        {
            logger.LogWarning("Destination {Destination} in cluster {Cluster} is now {State}",
                destination.Address, cluster.Id, healthy ? "healthy" : "unhealthy");
        }
        destination.IsHealthy = healthy;
    }
}
