# syntax=docker/dockerfile:1
# Build context is this project folder: docker build -t todo-api todo_API

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
COPY ["todo_API.csproj", "./"]
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore "todo_API.csproj" -a $TARGETARCH

# 2) Publish
COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish "todo_API.csproj" \
      -c $BUILD_CONFIGURATION \
      -a $TARGETARCH \
      --no-restore \
      -o /app/publish \
 && rm -f /app/publish/*.dbg

# ============ Runtime stage ============
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["./todo_API"]
