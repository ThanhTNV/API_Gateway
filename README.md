# API Gateway Lab

A hands-on ASP.NET Core 10 lab for routing, rate limiting, backend health checks, and runtime configuration. Each cluster has one required backend destination. In Kubernetes, that destination is a Service; Kubernetes distributes connections across the todo Pods.

## Features

- **Rate limiting**: fixed window, sliding window, or token bucket, partitioned by IP, API key, or user. Rejected requests receive 429 and a Retry-After header.
- **Single destination per cluster**: one absolute HTTP or HTTPS URL; gateway load-balancing algorithms have been removed.
- **Backend health checks**: periodic probes update cluster health, and routes referencing an unhealthy cluster return 503.
- **Configuration validation**: invalid destinations and unknown configuration properties are rejected.
- **Runtime reconfiguration**: an authenticated admin API validates and replaces configuration, persisting accepted changes before applying them.
- **Container deployment**: Docker Compose runs one todo backend; Kubernetes runs two todo replicas behind a Service.

## Architecture

```text
Docker Compose:
Client -> Gateway -> todo container

Kubernetes:
Client -> gateway Service -> Gateway Pod -> todo Service -> ready todo Pods
```

The gateway routes requests and enforces rate limits. It does not select individual todo replicas. A Kubernetes Service distributes connections, so pooled HTTP connections may send multiple requests to the same Pod.

The gateway strips the configured route prefix before forwarding: `/api/todos` reaches the backend root, and `/api/todos/1` reaches `/1`. The sample todo API includes an `X-Served-By` response header identifying the instance.

## Projects and class responsibilities

| Location | Responsibility |
| --- | --- |
| [self_API_Gateway](self_API_Gateway/) | Gateway application |
| [todo_API](todo_API/) | Sample todo backend, including `/health` |
| [Configuration/GatewayOptions.cs](self_API_Gateway/Configuration/GatewayOptions.cs) | Route, cluster, health, rate-limit, and trusted-proxy configuration models |
| [Backends/ClusterState.cs](self_API_Gateway/Backends/ClusterState.cs) | Destination health state, cluster lookup, URL validation, and health preservation across reloads |
| [Routing/GatewayRuntime.cs](self_API_Gateway/Routing/GatewayRuntime.cs) | Loads configuration, builds snapshots, and publishes runtime updates |
| [Routing/GatewayEndpoints.cs](self_API_Gateway/Routing/GatewayEndpoints.cs) | Builds routes, attaches rate-limit metadata, checks backend health, and invokes forwarding |
| [Health/HealthCheckService.cs](self_API_Gateway/Health/HealthCheckService.cs) | Periodically probes each configured cluster destination |
| [Proxy/HttpForwarder.cs](self_API_Gateway/Proxy/HttpForwarder.cs) | Streams requests/responses and handles forwarding failures |
| [RateLimiting](self_API_Gateway/RateLimiting/) | Rate-limit policies and middleware configuration |
| [Admin/AdminEndpoints.cs](self_API_Gateway/Admin/AdminEndpoints.cs) | Authenticated GET/PUT configuration endpoints |
| [Configuration/ConfigChangeLog.cs](self_API_Gateway/Configuration/ConfigChangeLog.cs) | Persisted change records and strict source-generated JSON serialization |

## Run with Docker Compose

From the repository root:

```bash
docker compose up --build
```

Compose starts the gateway on port 8080 and one backend named `todo`, matching `http://todo:8080` in the gateway configuration.

```bash
curl http://localhost:8080/api/todos
curl http://localhost:8080/api/todos/1
```

Set `GATEWAY_PORT` to override the published gateway port.

## Run with Kubernetes

The manifests are categorized as follows:

```text
k8s/
├── configmaps/
│   └── gateway-configmap.yaml
├── networkings/
│   ├── gateway-service.yaml
│   └── todo-service.yaml
├── storages/
│   ├── gateway-data-pvc.yaml
│   └── gateway-logs-pvc.yaml
└── workloads/
    ├── gateway-deployment.yaml
    └── todo-deployment.yaml
```

Build the images used by the Deployments:

```bash
docker build -t todo-api:dev ./todo_API
docker build -t todo-gateway:dev ./self_API_Gateway
```

Load these images into your local cluster using its image-loading mechanism. For a remote cluster, push them to a registry and update the Deployment image references. The PVCs require a default StorageClass capable of provisioning storage, or matching pre-provisioned volumes.

Create the required admin Secret. An empty key keeps the admin API disabled:

```bash
kubectl create secret generic gateway-admin --from-literal=api-key=""
kubectl apply -f k8s/ --recursive
kubectl rollout status deployment/todo
kubectl rollout status deployment/gateway
kubectl port-forward service/gateway 8080:8080
```

Create the Secret and resources in the same namespace. If the Secret already exists, reuse it or update it rather than running create again. To enable the admin API, supply a non-empty key securely.

Keep port forwarding running and use the same localhost URLs as the Compose example.

The todo Deployment has two replicas. Both Deployments specify CPU/memory requests of 100m/128Mi and limits of 200m/256Mi per container. The gateway runs one replica with the Recreate strategy and persistent logs/data volumes.

The gateway Deployment loads `gateway-config` through `envFrom`. Its route identifier key is `Gateway__Routes__0__Id`. ConfigMap values override matching appsettings values at startup; they do not replace the entire configuration object. After changing ConfigMap environment values, apply them and restart the gateway:

```bash
kubectl apply -f k8s/configmaps/gateway-configmap.yaml
kubectl rollout restart deployment/gateway
kubectl rollout status deployment/gateway
```

Persisted admin configuration takes precedence over these startup settings; see the runtime configuration section.

## Configuration

Routes, clusters, and rate-limit policies are defined under `Gateway` in [appsettings.json](self_API_Gateway/appsettings.json). A cluster has exactly one required Destination string:

```json
{
  "Destination": "http://todo:8080",
  "HealthCheck": {
    "Path": "/health",
    "IntervalSeconds": 10,
    "TimeoutSeconds": 2
  }
}
```

This object belongs under `Gateway.Clusters.<cluster-id>`. Each route references that ID through `ClusterId`. Use a namespace-qualified Service hostname when the backend is in another Kubernetes namespace.

Missing, null, empty, or whitespace destinations and non-HTTP(S) URLs fail validation. `Destinations` arrays and `LoadBalancing` are obsolete and rejected. Environment overrides use singular keys such as `Gateway__Clusters__todo-cluster__Destination`.

`KnownProxies` and `KnownNetworks` identify trusted HTTP proxies in front of the gateway. Forwarded Headers Middleware runs before rate limiting so IP partitions can use the forwarded client address. A downstream todo Service does not belong in that trust list merely because the gateway forwards to it.

## Health behavior

- Active checks are optional and run per cluster, starting with an immediate probe. Routes sharing a cluster share its health status.
- A probe sends GET to Destination plus HealthCheck.Path. A successful HTTP status marks the destination healthy; unsuccessful responses, timeouts, or probe failures mark it unhealthy.
- Destinations start healthy so traffic can flow before the first probe. Probe interval and timeout are clamped to at least one second.
- Unhealthy destinations cause matching gateway routes to return 503. Probes continue and restore traffic after success.
- Forwarding connection failures return 502 and mark the destination unhealthy only when active checks are configured. Client cancellation does not mark it unhealthy. Backend HTTP 5xx responses are logged and forwarded.
- Configuration replacement cancels the previous probe loops. An unchanged destination can retain its health state when active checks remain enabled.

A health request through `todo` reaches one backend Pod; it does not establish the health of every replica. Kubernetes readiness probes check individual Pods and control their eligibility for Service traffic. The current manifests use TCP readiness probes, which check the listening port rather than `/health`. They do not configure liveness probes.

One passive forwarding failure currently blocks every route using that cluster until a successful probe. Failure/recovery thresholds are a possible follow-up improvement. If you add gateway liveness checks, keep them independent of todo availability.

## Try the rate limiter

```powershell
.\test-rate-limit.ps1 -BaseUrl http://localhost:8080/api/todos -Requests 300
```

The script sends concurrent bursts and reports response codes, including 429 rejections. Its default request count is 300, above the default token-bucket burst capacity of 100.

## Change configuration at runtime

The admin API is disabled until Admin:ApiKey is non-empty. With Compose:

```powershell
$env:GATEWAY_ADMIN_KEY = 'replace-with-a-long-random-secret'
docker compose up --build
```

Read the live configuration, edit it, and submit the whole object back. These examples use `curl.exe` in PowerShell:

```powershell
curl.exe -H "X-Admin-Key: replace-with-a-long-random-secret" http://localhost:8080/admin/config -o gateway.json
curl.exe -X PUT -H "X-Admin-Key: replace-with-a-long-random-secret" -H "Content-Type: application/json" --data-binary "@gateway.json" http://localhost:8080/admin/config
```

- PUT accepts the Gateway configuration object itself, without an outer Gateway wrapper, and replaces the whole configuration.
- Routes, clusters, and policies take effect immediately. KnownProxies and KnownNetworks require a restart.
- Invalid JSON, unknown properties, or invalid configuration return 400 without changing the live snapshot or appending an accepted record.
- Accepted changes are appended and flushed to `.conf_change` before publication. Each record includes a sequence number, timestamp, source, caller IP, and configuration.
- Startup restores the last readable saved record before considering appsettings/environment settings. File configuration changes are not automatically published as runtime updates by GatewayRuntime.
- The container image sets Admin__ChangeLogPath to `data/.conf_change`; both deployment modes mount persistent data at `/app/data`, so the saved log is `/app/data/.conf_change`.
- To deliberately return to file/environment configuration, stop the gateway and back up/archive its persisted log before restarting.

## Migrating old persisted configuration

The single-destination implementation is complete, but installations with old admin records require a data migration. Old records using Destinations or LoadBalancing fail strict JSON deserialization. The log reader skips unreadable records, which can cause fallback to an older readable configuration or appsettings; do not use that fallback as your migration plan.

- [ ] Stop configuration writes and back up the actual persisted `.conf_change` file.
- [ ] Preserve the latest effective routes, rate-limit policies, and proxy trust settings.
- [ ] Replace each old destination list with the intended Service URL under Destination, and remove LoadBalancing.
- [ ] Either convert saved records while preserving metadata and one-record-per-line formatting, or archive the old log and place the migrated effective configuration in startup configuration before starting a new log.
- [ ] Verify the configuration returned by admin GET after startup and after another restart.

## Verification checklist

The implementation changes are recorded in Git; the checklist below is for verifying your environment and does not claim tests have been run.

- [ ] Build with `dotnet build self_API_Gateway/self_API_Gateway.csproj` and verify the Native AOT Docker build.
- [ ] Check startup and admin validation for missing destinations, arrays, invalid URLs, and obsolete properties.
- [ ] Verify invalid admin updates leave live configuration unchanged; verify valid updates survive restart.
- [ ] Verify healthy forwarding, 503 while unhealthy, and recovery after successful probes.
- [ ] Verify connection failures, client cancellation, streaming, query strings, prefix stripping, and rate limiting.
- [ ] Verify runtime destination changes stop old probes and start new probes.
- [ ] In Kubernetes, test removal of one todo Pod, no ready todo Pods, and recovery.
- [ ] Inspect effective configuration when ConfigMap changes appear ineffective; a saved admin record may take precedence.

## Recent implementation history

| Commit | Change |
| --- | --- |
| `929ee34` | Connected the gateway ConfigMap, corrected the route Id key, switched Compose to one todo backend, and raised the rate-limit script's default burst to 300 |
| `e442687` | Replaced gateway load balancing with one destination per cluster; retained health checks and enabled strict startup binding |
| `0fc1cf3` | Added categorized Kubernetes Deployments, Services, and PVCs |
| `315e5df` | Added authenticated admin configuration updates and persisted runtime snapshots |
