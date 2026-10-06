# API Gateway Lab

A hands-on lab project: building a small API gateway from scratch with ASP.NET Core, to learn how rate limiting and load balancing work under the hood.

## Features

- **Rate limiting** — fixed window, sliding window, or token bucket; partitioned by IP, API key, or user. Rejected requests get `429` with a `Retry-After` header.
- **Load balancing** — round robin, least requests, power of two choices, or random.
- **Health checks** — unhealthy destinations are taken out of rotation.
- **Config-driven** — routes, clusters, and policies live in [appsettings.json](self_API_Gateway/appsettings.json).
- **Runtime reconfiguration** — an authenticated admin API replaces the configuration without a restart, recorded in a write-ahead log.

## Projects

| Folder | Description |
| --- | --- |
| [self_API_Gateway](self_API_Gateway/) | The gateway |
| [todo_API](todo_API/) | A sample todo API used as the backend |

## Run

```bash
docker compose up --build
```

This starts the gateway on port 8080 in front of two todo API instances:

```bash
curl http://localhost:8080/api/todos
curl http://localhost:8080/api/todos/1
```

## Try the rate limiter

```powershell
.\test-rate-limit.ps1 -BaseUrl http://localhost:8080/api/todos
```

It fires bursts of concurrent requests and reports how many were rejected with `429`.

## Change the configuration at runtime

The admin API is off until an API key is set (`Admin:ApiKey`, or `GATEWAY_ADMIN_KEY` with Docker Compose):

```powershell
$env:GATEWAY_ADMIN_KEY = 'a-long-random-secret'
docker compose up --build
```

Read the live `Gateway` configuration, edit it, and submit it back:

```bash
curl -H "X-Admin-Key: a-long-random-secret" http://localhost:8080/admin/config -o gateway.json
curl -X PUT -H "X-Admin-Key: a-long-random-secret" --data-binary @gateway.json http://localhost:8080/admin/config
```

- `PUT` replaces the whole configuration. Routes, clusters, and rate limit policies take effect immediately; `KnownProxies` and `KnownNetworks` need a restart.
- An invalid configuration is rejected with `400` and nothing changes.
- Every accepted change is appended to `.conf_change` (one JSON record per line, with sequence number, time, and caller IP) before it is applied. On startup the last record in that file is the configuration used, taking precedence over the `Gateway` section of `appsettings.json`. Delete the file to go back to `appsettings.json`.
- In Docker the file lives in the `gateway-data` volume at `/app/data/.conf_change`.
