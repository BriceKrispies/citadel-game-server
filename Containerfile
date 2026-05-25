# syntax=docker/dockerfile:1
# =============================================================================
# Citadel game-server host — hardened container image.
#
# Multi-stage:
#   build   : full .NET 10 SDK, restores + publishes GameServer.Host (Release).
#   runtime : chiseled/distroless ASP.NET 10 base. This base image ALREADY runs
#             as a non-root "app" user (UID 1654) and ships no shell/package
#             manager. We deliberately do NOT add `USER root` — the process must
#             stay non-root.
#
# Security posture:
#   - No secrets are baked into the image. The join-token secret and the
#     control-plane API keys are injected at runtime via environment variables
#     (Auth__JoinTokenSecret, ControlPlane__ApiKeys__N__*). The host FAILS CLOSED
#     in Production if they are missing (see src/GameServer.Host/Program.cs).
#   - No dev keys, no dev endpoints: ASPNETCORE_ENVIRONMENT=Production turns off
#     the dev-seeded API keys and gates off /ws and /sim/telemetry.
#
# ## TLS trust boundary
#   This container serves PLAINTEXT HTTP on :8080 only. TLS is terminated upstream
#   at the AWS ECS Application Load Balancer (ALB); the ALB forwards decrypted
#   HTTP to the task on :8080 inside the trusted VPC. The container intentionally
#   does not manage certificates or bind an HTTPS port.
# =============================================================================

# ---- build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# The Host's build pulls in the whole src tree (project references) plus the repo
# Roslyn analyzer (tools/) and its config (repo-analyzers.json) wired by
# src/Directory.Build.targets. Co-located *.Tests.cs are NOT compiled into the
# host but ARE consumed by the analyzer as AdditionalFiles, so the build context
# must keep them (see .containerignore). Copy the build inputs, then restore once.
COPY Directory.Build.props repo-analyzers.json ./
COPY src/ ./src/
COPY tools/ ./tools/
# GameServer.Protocol compiles the canonical realtime .proto at build time.
COPY contracts/ ./contracts/

RUN dotnet restore src/GameServer.Host/GameServer.Host.csproj

# Publish a framework-dependent build; the runtime base carries the ASP.NET shared
# framework. No self-contained/trimmed publish needed for the chiseled aspnet base.
RUN dotnet publish src/GameServer.Host/GameServer.Host.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ---- runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Production posture: fail-closed config + dev endpoints gated off. Bind 0.0.0.0:8080
# (the chiseled base sets a non-root user already, which cannot bind <1024).
# Program.cs honors ASPNETCORE_HTTP_PORTS instead of its localhost:5000 dev default.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "GameServer.Host.dll"]
