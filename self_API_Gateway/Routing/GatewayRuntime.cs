using System.Net;
using Microsoft.Extensions.Primitives;
using self_API_Gateway.Configuration;
using self_API_Gateway.LoadBalancing;
using self_API_Gateway.Proxy;

namespace self_API_Gateway.Routing;

// Everything derived from one version of the configuration. Immutable: a change builds a new snapshot.
public sealed class GatewaySnapshot(long seq, GatewayOptions options, ClusterRegistry clusters, IReadOnlyList<Endpoint> endpoints)
{
    private readonly CancellationTokenSource _replaced = new();

    public long Seq { get; } = seq;
    public GatewayOptions Options { get; } = options;
    public ClusterRegistry Clusters { get; } = clusters;
    public IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;

    // Cancelled once a newer snapshot has taken over.
    public CancellationToken Replaced => _replaced.Token;

    internal void MarkReplaced() => _replaced.Cancel();
}

// Owns the live configuration and swaps it atomically when a new one is applied.
public sealed class GatewayRuntime
{
    private readonly Lock _applyLock = new();
    private readonly ConfigChangeLog _changeLog;
    private readonly HttpForwarder _forwarder;
    private readonly ILogger<GatewayRuntime> _logger;
    private readonly GatewayEndpointDataSource _dataSource;
    private GatewaySnapshot _current;

    public GatewayRuntime(IConfiguration configuration, IHostEnvironment environment, HttpForwarder forwarder, ILogger<GatewayRuntime> logger)
    {
        _forwarder = forwarder;
        _logger = logger;

        var path = configuration["Admin:ChangeLogPath"] is { Length: > 0 } configured ? configured : ".conf_change";
        _changeLog = new ConfigChangeLog(Path.Combine(environment.ContentRootPath, path));

        // The last change made through the admin API wins over the Gateway section of appsettings.json.
        var last = _changeLog.ReadLast(out var skipped);
        if (skipped > 0)
        {
            logger.LogWarning("Skipped {Count} unreadable record(s) at the end of {Path}", skipped, _changeLog.FilePath);
        }

        GatewayOptions options;
        if (last is not null)
        {
            options = last.Config;
            logger.LogInformation("Gateway configuration restored from {Path} (change #{Seq} made at {Time})", _changeLog.FilePath, last.Seq, last.Time);
        }
        else
        {
            options = configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        }

        StartupOptions = options;
        _current = BuildSnapshot(last?.Seq ?? 0, options, previous: null);
        _dataSource = new GatewayEndpointDataSource(_current.Endpoints);
    }

    public GatewaySnapshot Current => Volatile.Read(ref _current);

    // The configuration the process started with, for settings that are only read at startup.
    public GatewayOptions StartupOptions { get; }

    public EndpointDataSource EndpointDataSource => _dataSource;

    // Validates, records the change in the write-ahead log, then makes it live.
    // Throws GatewayConfigException, leaving everything untouched, when the configuration is invalid.
    public ConfigChangeResult Apply(GatewayOptions options, string source, string? remoteIp)
    {
        lock (_applyLock)
        {
            var previous = _current;
            var next = BuildSnapshot(previous.Seq + 1, options, previous.Clusters);

            _changeLog.Append(new ConfigChangeRecord
            {
                Seq = next.Seq,
                Time = DateTimeOffset.UtcNow,
                Source = source,
                RemoteIp = remoteIp,
                Config = options
            });

            Volatile.Write(ref _current, next);
            _dataSource.Update(next.Endpoints);
            previous.MarkReplaced();

            _logger.LogInformation("Gateway configuration change #{Seq} applied: {Routes} route(s), {Clusters} cluster(s)",
                next.Seq, options.Routes.Count, options.Clusters.Count);

            var restartRequired =
                !options.KnownProxies.SequenceEqual(StartupOptions.KnownProxies) ||
                !options.KnownNetworks.SequenceEqual(StartupOptions.KnownNetworks);

            return new ConfigChangeResult
            {
                Seq = next.Seq,
                RestartRequired = restartRequired,
                Message = restartRequired
                    ? "Applied. KnownProxies and KnownNetworks take effect after a restart."
                    : "Applied."
            };
        }
    }

    private GatewaySnapshot BuildSnapshot(long seq, GatewayOptions options, ClusterRegistry? previous)
    {
        if (options.Routes is null || options.Clusters is null || options.RateLimitPolicies is null ||
            options.KnownProxies is null || options.KnownNetworks is null)
        {
            throw new GatewayConfigException("Routes, Clusters, RateLimitPolicies, KnownProxies and KnownNetworks must not be null.");
        }

        // Checked now so a bad value cannot stop the next startup.
        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
                throw new GatewayConfigException($"KnownProxies: '{proxy}' is not an IP address.");
        }
        foreach (var network in options.KnownNetworks)
        {
            if (!IPNetwork.TryParse(network, out _))
                throw new GatewayConfigException($"KnownNetworks: '{network}' is not a network in CIDR notation.");
        }

        var clusters = new ClusterRegistry(options, previous);
        var endpoints = GatewayEndpoints.Build(options, clusters, _forwarder, _logger);
        return new GatewaySnapshot(seq, options, clusters, endpoints);
    }

    private sealed class GatewayEndpointDataSource : EndpointDataSource
    {
        private readonly Lock _lock = new();
        private IReadOnlyList<Endpoint> _endpoints;
        private CancellationTokenSource _cts = new();
        private IChangeToken _changeToken;

        public GatewayEndpointDataSource(IReadOnlyList<Endpoint> endpoints)
        {
            _endpoints = endpoints;
            _changeToken = new CancellationChangeToken(_cts.Token);
        }

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get { lock (_lock) return _endpoints; }
        }

        public override IChangeToken GetChangeToken()
        {
            lock (_lock) return _changeToken;
        }

        public void Update(IReadOnlyList<Endpoint> endpoints)
        {
            CancellationTokenSource old;
            lock (_lock)
            {
                _endpoints = endpoints;
                old = _cts;
                _cts = new CancellationTokenSource();
                _changeToken = new CancellationChangeToken(_cts.Token);
            }
            // Tells routing to re-read Endpoints; signalled after the new list and token are in place.
            old.Cancel();
        }
    }
}
