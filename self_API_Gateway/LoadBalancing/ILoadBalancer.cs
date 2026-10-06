namespace self_API_Gateway.LoadBalancing;

public interface ILoadBalancer
{
    DestinationState? Pick(IReadOnlyList<DestinationState> healthy);
}
