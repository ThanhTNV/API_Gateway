# syntax=docker/dockerfile:1
# Build context is this project folder: docker build -t api-gateway self_API_Gateway

ARG DOTNET_VERSION=10.0

# ============ Build stage ============
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# 1) Restore
COPY ["self_API_Gateway.csproj", "./"]
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore "self_API_Gateway.csproj" -a $TARGETARCH

# 2) Publish
COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish "self_API_Gateway.csproj" \
      -c $BUILD_CONFIGURATION \
      -a $TARGETARCH \
      --no-restore \
      -o /app/publish \
 && rm -f /app/publish/*.dbg \
 && mkdir /app/logs /app/data

# ============ Runtime stage ============
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
# The chiseled image has no shell and runs as non-root: bring in directories the app user can write to.
COPY --from=build --chown=$APP_UID:$APP_UID /app/logs ./logs
COPY --from=build --chown=$APP_UID:$APP_UID /app/data ./data
ENV ASPNETCORE_HTTP_PORTS=8080
# Configuration change log; mount a volume at /app/data to keep admin changes across containers.
ENV Admin__ChangeLogPath=data/.conf_change
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["./self_API_Gateway"]
