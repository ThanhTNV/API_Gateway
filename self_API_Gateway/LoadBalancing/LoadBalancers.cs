namespace self_API_Gateway.LoadBalancing;

public sealed class RoundRobinLoadBalancer : ILoadBalancer
{
    private int _i = -1;

    public DestinationState? Pick(IReadOnlyList<DestinationState> healthy)
    {
        if (healthy.Count == 0) return null;
        // Mask off the sign bit so the index stays positive when the counter overflows.
        var i = Interlocked.Increment(ref _i) & int.MaxValue;
        return healthy[i % healthy.Count];
    }
}

public sealed class LeastRequestsLoadBalancer : ILoadBalancer
{
    public DestinationState? Pick(IReadOnlyList<DestinationState> healthy)
    {
        DestinationState? best = null;
        var bestActive = int.MaxValue;
        for (var i = 0; i < healthy.Count; i++)
        {
            var active = healthy[i].ActiveRequests;
            if (active < bestActive)
            {
                best = healthy[i];
                bestActive = active;
            }
        }
        return best;
    }
}

public sealed class PowerOfTwoChoicesLoadBalancer : ILoadBalancer
{
    public DestinationState? Pick(IReadOnlyList<DestinationState> healthy)
    {
        if (healthy.Count == 0) return null;
        if (healthy.Count == 1) return healthy[0];

        // Two distinct random candidates.
        var i = Random.Shared.Next(healthy.Count);
        var j = Random.Shared.Next(healthy.Count - 1);
        if (j >= i) j++;

        var a = healthy[i];
        var b = healthy[j];
        return a.ActiveRequests <= b.ActiveRequests ? a : b;
    }
}

public sealed class RandomLoadBalancer : ILoadBalancer
{
    public DestinationState? Pick(IReadOnlyList<DestinationState> healthy) =>
        healthy.Count == 0 ? null : healthy[Random.Shared.Next(healthy.Count)];
}

public static class LoadBalancerFactory
{
    // A plain switch rather than reflection-based activation, which isn't AOT-safe.
    public static ILoadBalancer Create(string? strategy) => strategy?.ToLowerInvariant() switch
    {
        "roundrobin" => new RoundRobinLoadBalancer(),
        "leastrequests" => new LeastRequestsLoadBalancer(),
        null or "" or "poweroftwochoices" => new PowerOfTwoChoicesLoadBalancer(),
        "random" => new RandomLoadBalancer(),
        _ => throw new InvalidOperationException(
            $"Unknown load balancing strategy '{strategy}'. Expected RoundRobin, LeastRequests, PowerOfTwoChoices or Random.")
    };
}
