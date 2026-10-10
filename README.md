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

## Choose your deployment environment

Run commands from the repository root in PowerShell. The checked-in Kubernetes manifests target DigitalOcean. Docker Compose does not read any files under `k8s/`.

| File | Docker Compose | Minikube | DigitalOcean Kubernetes |
| --- | --- | --- | --- |
| `docker-compose.yml` | Used: builds gateway and todo locally | Not used | Not used |
| `self_API_Gateway/appsettings.json` | Backend `http://todo:8080`; empty proxy trust lists for direct local access | Same backend; ConfigMap overrides matching settings | Same backend; ConfigMap provides cloud settings |
| `k8s/workloads/todo-deployment.yaml` | Not used | Image `todo-api:dev` | Registry image with a release tag |
| `k8s/workloads/gateway-deployment.yaml` | Not used | Image `todo-gateway:dev` | Registry image with a release tag |
| `k8s/networkings/gateway-service.yaml` | Not used | `ClusterIP`; no cloud annotations; port 80 -> 8080 | `LoadBalancer`; DigitalOcean HTTP annotations; port 80 -> 8080 |
| `k8s/networkings/todo-service.yaml` | Not used | Keep `ClusterIP`, port 8080 | Keep `ClusterIP`, port 8080 |
| `k8s/storages/gateway-data-pvc.yaml` and `gateway-logs-pvc.yaml` | Not used: Compose named volumes | Omit cloud StorageClass to use Minikube default | `storageClassName: do-block-storage` |
| `k8s/config/gateway-configmap.yaml` | Not used | Remove cloud `Gateway__KnownNetworks__0` override | Set verified trusted upstream network for this cluster |
| `k8s/config/gateway-secret.yaml` | Not used: `GATEWAY_ADMIN_KEY` supplies the key | Optional Secret template; name `gateway-admin`, key `api-key` | Same; keep real credentials out of Git |
| `test-rate-limit.ps1` | Pass localhost URL | Pass localhost port-forward URL | Pass gateway external URL |

The commands below apply the ConfigMap, workloads, networking, and storage separately from the Secret. This avoids overwriting an existing admin key by applying a checked-in Secret template. All resources use the `default` namespace in these examples.

Saved `/app/data/.conf_change` admin configuration takes precedence over appsettings and ConfigMap settings. If a setting appears unchanged, inspect the live admin configuration before assuming the deployment failed. Do not delete persistent data to switch environments.

## Local option 1: Docker Compose

### 1. Check the files

In `self_API_Gateway/appsettings.json`, keep:

```json
"Destination": "http://todo:8080"
```

For direct local access, keep `Gateway.KnownProxies` and `Gateway.KnownNetworks` empty unless you intentionally put another trusted proxy in front of the gateway. The Compose backend service is named `todo`. No Kubernetes manifest changes are needed for this option.

### 2. Start and test

Start Docker Desktop with Linux containers, then run:

```powershell
docker compose up --build -d
docker compose ps
curl.exe http://localhost:8080/api/todos
.\test-rate-limit.ps1 -BaseUrl http://localhost:8080/api/todos -Requests 300
```

Optional environment settings, set before starting Compose:

```powershell
$env:GATEWAY_PORT = '8080'
$env:GATEWAY_ADMIN_KEY = 'replace-with-a-long-random-secret'
```

Without a non-empty admin key, the admin API is disabled. To inspect logs or stop containers:

```powershell
docker compose logs --tail=100 gateway
docker compose down
```

`down` without `--volumes` preserves named volumes. Re-run `docker compose up --build -d` after application or appsettings changes.

## Local option 2: Minikube

### 1. Start and select the local cluster

```powershell
minikube start
kubectl config use-context minikube
kubectl config current-context
kubectl get storageclass
```

Confirm the context is Minikube before applying resources.

### 2. Change the Kubernetes files for local use

In `k8s/workloads/todo-deployment.yaml`, set the container fields:

```yaml
image: todo-api:dev
imagePullPolicy: IfNotPresent
```

In `k8s/workloads/gateway-deployment.yaml`:

```yaml
image: todo-gateway:dev
imagePullPolicy: IfNotPresent
```

Keep gateway `envFrom.configMapRef.name: gateway-config`, the `gateway-admin` Secret reference, and existing volume mounts.

Replace `k8s/networkings/gateway-service.yaml` with:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: gateway
spec:
  type: ClusterIP
  selector:
    app: gateway
  ports:
    - name: http
      port: 80
      targetPort: 8080
```

In both `k8s/storages/gateway-data-pvc.yaml` and `gateway-logs-pvc.yaml`, remove `storageClassName: do-block-storage`. Keep the existing claim name, `ReadWriteOnce`, and `1Gi` request. Omitting the field uses the cluster's default StorageClass; check one exists with `kubectl get storageclass`.

In `k8s/config/gateway-configmap.yaml`, keep destination `http://todo:8080`, the route, and rate-limit settings. Remove `Gateway__KnownNetworks__0: "::ffff:10.104.0.0/116"` for direct port-forward access. It belongs to the cloud environment. Keep `k8s/networkings/todo-service.yaml` unchanged.

StorageClass changes apply to new claims. Existing bound PVCs generally cannot change StorageClass in place: preserve their data and plan a migration if you already created claims with different storage settings.

### 3. Build, load, and deploy

```powershell
docker build -t todo-api:dev ./todo_API
docker build -t todo-gateway:dev ./self_API_Gateway
minikube image load todo-api:dev
minikube image load todo-gateway:dev
```

On the first deployment, create the required Secret (skip if it already exists):

```powershell
kubectl create secret generic gateway-admin --from-literal=api-key=""
```

Then apply:

```powershell
kubectl apply -f k8s/config/gateway-configmap.yaml -f k8s/workloads/ -f k8s/networkings/ -f k8s/storages/
kubectl rollout status deployment/todo --timeout=300s
kubectl rollout status deployment/gateway --timeout=300s
kubectl get pods,services,pvc
kubectl port-forward service/gateway 8080:80
```

Keep port forwarding running. In another terminal:

```powershell
curl.exe http://localhost:8080/api/todos
.\test-rate-limit.ps1 -BaseUrl http://localhost:8080/api/todos -Requests 300
```

For later application changes, use a new local tag (for example `:dev2`), build/load it, update the corresponding Deployment image, and apply that file. This avoids ambiguous cached-image behavior. A ConfigMap environment update requires applying the ConfigMap and restarting the gateway.

## Cloud: DigitalOcean Kubernetes

### 1. Create infrastructure and connect

Create a managed Kubernetes cluster and Container Registry in DigitalOcean. Ensure the registry plan supports both image repositories. In cluster Settings, enable DigitalOcean Container Registry Integration to permit private image pulls. Install Docker, kubectl, and doctl locally. Nodes, load balancer, volumes, and registry can incur charges.

Replace the example names with your own:

```powershell
doctl auth init
doctl kubernetes cluster kubeconfig save todo-cluster
kubectl config current-context
kubectl get nodes
doctl registries login todo-cluster-registry
```

Verify the context identifies DigitalOcean, not Minikube.

### 2. Build and push release images

Keep the application destination `http://todo:8080`. Use a new release tag each time:

```powershell
docker build -t registry.digitalocean.com/todo-cluster-registry/todo-api:v2 ./todo_API
docker build -t registry.digitalocean.com/todo-cluster-registry/todo-gateway:v2 ./self_API_Gateway
docker push registry.digitalocean.com/todo-cluster-registry/todo-api:v2
docker push registry.digitalocean.com/todo-cluster-registry/todo-gateway:v2
```

Both pushes must succeed before deployment. The registry name in every command must match your actual registry.

### 3. Change the Kubernetes files for cloud use

In `k8s/workloads/todo-deployment.yaml`:

```yaml
image: registry.digitalocean.com/todo-cluster-registry/todo-api:v2
imagePullPolicy: IfNotPresent
```

In `k8s/workloads/gateway-deployment.yaml`:

```yaml
image: registry.digitalocean.com/todo-cluster-registry/todo-gateway:v2
imagePullPolicy: IfNotPresent
```

Replace `k8s/networkings/gateway-service.yaml` with:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: gateway
  annotations:
    service.beta.kubernetes.io/do-loadbalancer-type: "REGIONAL"
    service.beta.kubernetes.io/do-loadbalancer-protocol: "http"
spec:
  type: LoadBalancer
  selector:
    app: gateway
  ports:
    - name: http
      port: 80
      targetPort: 8080
```

The protocol annotation controls traffic forwarding, not just health checks. HTTP forwarding supplies `X-Forwarded-For`. Keep the todo Service internal and unchanged.

In both `k8s/storages/gateway-data-pvc.yaml` and `gateway-logs-pvc.yaml`, add under `spec`:

```yaml
storageClassName: do-block-storage
```

Keep `ReadWriteOnce` and the storage request. The gateway stays at one replica with `Recreate` for its current file-based data. Kubernetes automatically provisions two DigitalOcean block volumes and a load balancer when you apply the resources. Local data is not migrated automatically.

In `k8s/config/gateway-configmap.yaml`, keep the route, rate policy, and `http://todo:8080` destination. Configure a verified trusted upstream network for this cluster. For the current cluster, DigitalOcean's cluster Networking panel shows node network `10.104.0.0/20`; incoming peers were IPv4-mapped IPv6 addresses, so the setting is:

```yaml
Gateway__KnownNetworks__0: "::ffff:10.104.0.0/116"
```

Do not copy that range into a different cluster without verifying it. It trusts every peer in the network, so restrict gateway access to the intended proxy path; see the client-IP section below. Keep real Secret values out of Git. The first-deployment example below leaves the admin API disabled.

### 4. Apply and verify

Create the Secret once (skip if it already exists):

```powershell
kubectl create secret generic gateway-admin --from-literal=api-key=""
```

Deploy:

```powershell
kubectl apply -f k8s/config/gateway-configmap.yaml -f k8s/workloads/ -f k8s/networkings/ -f k8s/storages/
kubectl rollout status deployment/todo --timeout=300s
kubectl rollout status deployment/gateway --timeout=300s
kubectl get pods,pvc
kubectl get service gateway -w
```

Wait for Pods to be ready, PVCs to show Bound, and the Service to obtain an EXTERNAL-IP. Stop watching with Ctrl+C, then replace EXTERNAL-IP in:

```powershell
curl.exe http://EXTERNAL-IP/api/todos
.\test-rate-limit.ps1 -BaseUrl http://EXTERNAL-IP/api/todos -Requests 300
```

Public per-IP testing should show the expected client IP at the limiter, some 429 responses under a sufficiently fast burst, and a successful request after replenishment. Add a domain and HTTPS before sending sensitive traffic or admin credentials over the public endpoint.

### 5. Updating an existing deployment

| What changed | Required action |
| --- | --- |
| Gateway code or image-bundled appsettings | Build/push a new tag; update gateway Deployment image; apply Deployment; wait for rollout |
| Todo code | Build/push a new tag; update todo Deployment image; apply Deployment; wait for rollout |
| ConfigMap environment values, including KnownNetworks | Apply ConfigMap; restart gateway; wait for rollout |
| Admin Secret consumed as environment variables | Update Secret securely; restart gateway |
| Service or load-balancer annotation | Apply Service; wait for cloud reconciliation; no Deployment restart needed |
| Bound PVC StorageClass | Plan data migration; changing the YAML alone cannot move the existing volume |

For a ConfigMap update:

```powershell
kubectl apply -f k8s/config/gateway-configmap.yaml
kubectl rollout restart deployment/gateway
kubectl rollout status deployment/gateway
```

For a load-balancer protocol update:

```powershell
kubectl apply -f k8s/networkings/gateway-service.yaml
kubectl describe service gateway
```

Check recent events and the load balancer's forwarding rules in DigitalOcean. An annotation being present only confirms the desired configuration; an unchanged public IP does not confirm update completion.

A restart with `IfNotPresent` does not guarantee fetching an overwritten tag. Prefer new tags for releases. When moving between local and cloud, select the correct context and restore all file settings in the comparison table before applying; never delete PVCs as a shortcut.

Official guides: [cluster connection](https://docs.digitalocean.com/products/kubernetes/how-to/connect-to-cluster/), [registry integration](https://docs.digitalocean.com/products/kubernetes/how-to/integrate-with-docr/), [load-balancer settings](https://docs.digitalocean.com/products/kubernetes/how-to/configure-load-balancers/), and [volumes](https://docs.digitalocean.com/products/kubernetes/how-to/add-volumes/).

## Client IP forwarding behind cloud proxies

Per-IP rate limiting uses `HttpContext.Connection.RemoteIpAddress`. Behind a cloud load balancer, that can be an internal proxy or Kubernetes node address. Requests from one client can then use several independent buckets, making a public burst appear unthrottled even though the same test through port forwarding produces 429s.

Configure both sides: the upstream HTTP proxy must send the original client information, and ASP.NET Core must trust the verified upstream addresses/networks. `UseForwardedHeaders()` runs before rate limiting. Keep using RemoteIpAddress as the limiter key; do not use an unvalidated raw X-Forwarded-For header.

The current ConfigMap trusts `::ffff:10.104.0.0/116`, the IPv4-mapped IPv6 representation of node network `10.104.0.0/20`. This is specific to the current deployment, not a universal cloud range. Obtain the relevant range from the cluster/network configuration and verify it matches incoming peer addresses. In this deployment, the node network is shown in the cluster's Networking panel. Other providers and proxy topologies may require different ranges or source-IP mechanisms.

Trusting a node network trusts forwarded headers from every address in that network. Restrict direct gateway/NodePort access to the intended proxy path and verify how the upstream proxy handles client-supplied forwarded headers. Do not trust all private networks or disable proxy validation to make a test pass.

After applying ConfigMap environment changes, restart the gateway and inspect the live admin configuration if enabled. A persisted admin record takes precedence over environment configuration. Validate through the public endpoint that RemoteIpAddress becomes the expected client address and the burst returns 429s. Forwarded Headers Middleware consumes processed XFF values, so an empty XFF after middleware can be normal. Log incoming peer/XFF before the middleware when troubleshooting, then remove per-request diagnostic warning logs.

Multiple gateway replicas have independent in-memory counters. Use a shared limiter store when one allowance must cover all replicas. The two todo replicas also need a shared database if they are expected to share mutable todo data.

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
| `1698a77` | Added cloud registry images, a public HTTP LoadBalancer Service, DigitalOcean block-storage classes, the config folder/Secret template, and deployment-specific trusted network configuration |
| `929ee34` | Connected the gateway ConfigMap, corrected the route Id key, switched Compose to one todo backend, and raised the rate-limit script's default burst to 300 |
| `e442687` | Replaced gateway load balancing with one destination per cluster; retained health checks and enabled strict startup binding |
| `0fc1cf3` | Added categorized Kubernetes Deployments, Services, and PVCs |
| `315e5df` | Added authenticated admin configuration updates and persisted runtime snapshots |
