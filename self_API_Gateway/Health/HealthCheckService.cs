using self_API_Gateway.Backends;
using self_API_Gateway.Routing;

namespace self_API_Gateway.Health;

public sealed class HealthCheckService(
    GatewayRuntime runtime,
    HttpMessageInvoker invoker,
    ILogger<HealthCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Probe the clusters of the current configuration, and start over whenever it is replaced.
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = runtime.Current;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, snapshot.Replaced);

            var loops = snapshot.Clusters.Clusters
                .Where(c => c.HealthCheck is not null)
                .Select(c => RunClusterAsync(c, cts.Token))
                .Append(WaitForCancellationAsync(cts.Token));
            await Task.WhenAll(loops);
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunClusterAsync(ClusterState cluster, CancellationToken token)
    {
        var config = cluster.HealthCheck!;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, config.IntervalSeconds)));

        try
        {
            do
            {
                await ProbeAsync(
                    cluster,
                    cluster.Destination,
                    config.Path,
                    timeout,
                    token);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeAsync(ClusterState cluster, DestinationState destination, string path, TimeSpan timeout, CancellationToken token)
    {
        bool healthy;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, destination.BaseUrl + path);
            using var response = await invoker.SendAsync(request, cts.Token);
            healthy = response.IsSuccessStatusCode;
        }
        catch (Exception) when (!token.IsCancellationRequested)
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
