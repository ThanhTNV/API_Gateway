# API Gateway Lab

A hands-on lab project: building a small API gateway from scratch with ASP.NET Core, to learn how rate limiting and load balancing work under the hood.

## Features

- **Rate limiting** — fixed window, sliding window, or token bucket; partitioned by IP, API key, or user. Rejected requests get `429` with a `Retry-After` header.
- **Load balancing** — round robin, least requests, power of two choices, or random.
- **Health checks** — unhealthy destinations are taken out of rotation.
- **Config-driven** — routes, clusters, and policies live in [appsettings.json](self_API_Gateway/appsettings.json).

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
