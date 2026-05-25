#!/usr/bin/env pwsh
# =============================================================================
# Run the integration suite (tests/GameServer.IntegrationTests) against a real
# container engine — Podman, NOT Docker Desktop.
#
#   pwsh scripts/integration-podman.ps1
#
# The suite's `RedisRoomDirectoryScenario` is a [SkippableFact]: with no reachable
# container engine it SKIPS (green) instead of proving the real Redis directory /
# placement + Lua. This script makes Podman's Docker-API-compatible socket
# reachable to Testcontainers .NET so the scenario RUNS (passes, not skips).
#
# ## Testcontainers -> Podman wiring
#   Testcontainers .NET talks the Docker Engine API. Podman exposes a
#   Docker-API-compatible endpoint via `podman machine`. On Windows that is the
#   named pipe `\\.\pipe\<machine>`; we resolve it at runtime from
#   `podman machine inspect` (NO hardcoded, user-specific path) and export it as
#   DOCKER_HOST=npipe://./pipe/<machine>.
#
# ## Ryuk trade-off (TESTCONTAINERS_RYUK_DISABLED=true)
#   Testcontainers normally launches "Ryuk" (a privileged reaper container) to
#   delete test containers after the run. Under rootless Podman Ryuk is flaky: it
#   wants to bind the engine socket and run with privileges rootless Podman won't
#   grant, which intermittently fails container startup. We therefore DISABLE Ryuk
#   and take ownership of teardown ourselves: the suite disposes each container it
#   starts (RedisContainer is `await using`), and as a backstop this script removes
#   any leftover `redis:7-alpine` containers in `finally`. Trade-off: if the test
#   process is hard-killed mid-run, a container could leak until the next run's
#   finally-sweep (or `podman ps -a`) reclaims it — acceptable for a dev/CI harness.
#
# ## Registry
#   `Testcontainers.Redis` pulls `docker.io/library/redis:7-alpine`. A clean Podman
#   install needs docker.io reachable; if your registries.conf lacks it, add:
#       unqualified-search-registries = ["docker.io"]
#   This script uses the fully-qualified image implicitly via Testcontainers, and
#   verifies the pull up front so a registry misconfig fails with a clear message.
# =============================================================================
[CmdletBinding()]
param(
    [string]$Machine = 'podman-machine-default',
    [string]$RedisImage = 'docker.io/library/redis:7-alpine'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Error "podman is not on PATH. Install Podman (https://podman.io) and retry."
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$integrationProj = Join-Path $repoRoot 'tests/GameServer.IntegrationTests/GameServer.IntegrationTests.csproj'

function Remove-LeakedRedisContainers {
    # Sweep any throwaway Redis containers from this suite (idempotent backstop for the
    # disabled Ryuk reaper). Testcontainers tags its containers; we match the image too.
    $ids = (podman ps -a --filter "ancestor=$RedisImage" --format '{{.ID}}') 2>$null
    if ($LASTEXITCODE -eq 0 -and $ids) {
        foreach ($id in ($ids -split "`n" | Where-Object { $_ -ne '' })) {
            podman rm -f $id *> $null
        }
    }
}

try {
    # 1) Ensure the Podman machine is up (idempotent).
    Write-Host "Ensuring Podman machine '$Machine' is running..." -ForegroundColor Cyan
    $state = (podman machine inspect $Machine --format '{{.State}}') 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Podman machine '$Machine' does not exist. Create it with: podman machine init; podman machine start"
        exit 1
    }
    if ($state -ne 'running') {
        Write-Host "Starting Podman machine '$Machine'..." -ForegroundColor Cyan
        podman machine start $Machine
        if ($LASTEXITCODE -ne 0) { throw "podman machine start failed (exit $LASTEXITCODE)." }
    }

    # 2) Resolve the Docker-API-compatible endpoint at runtime (no hardcoded path).
    #    On Windows the machine exposes a named pipe; map it to npipe:// for Testcontainers.
    $pipe = (podman machine inspect $Machine --format '{{.ConnectionInfo.PodmanPipe.Path}}') 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pipe)) {
        throw "Could not resolve the Podman API pipe from 'podman machine inspect $Machine'."
    }
    # \\.\pipe\podman-machine-default  ->  npipe://./pipe/podman-machine-default
    $pipeName = $pipe -replace '^\\\\\.\\pipe\\', ''
    $dockerHost = "npipe://./pipe/$pipeName"

    # 3) Verify the endpoint actually answers (fail fast with a clear message).
    Write-Host "Verifying Podman API pipe '$pipe' is reachable..." -ForegroundColor Cyan
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $client.Connect(5000)
        $client.Dispose()
    } catch {
        throw "Podman API pipe '$pipe' is not reachable: $($_.Exception.Message). Is the machine fully started?"
    }

    # 4) Make sure the Redis image can be pulled (surfaces registry misconfig early).
    Write-Host "Pulling $RedisImage (warm the cache, validate registry access)..." -ForegroundColor Cyan
    podman pull $RedisImage
    if ($LASTEXITCODE -ne 0) {
        throw "Could not pull $RedisImage. Check docker.io access / registries.conf (unqualified-search-registries)."
    }

    # 5) Export the Testcontainers -> Podman environment.
    $env:DOCKER_HOST = $dockerHost
    $env:TESTCONTAINERS_RYUK_DISABLED = 'true'
    Write-Host "DOCKER_HOST = $env:DOCKER_HOST" -ForegroundColor Green
    Write-Host "TESTCONTAINERS_RYUK_DISABLED = $env:TESTCONTAINERS_RYUK_DISABLED (script owns teardown)" -ForegroundColor Green

    # 6) Run the integration suite. With the engine reachable, RedisRoomDirectoryScenario RUNS.
    Write-Host "Running integration suite against Podman..." -ForegroundColor Cyan
    dotnet test $integrationProj
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) { throw "Integration suite failed (exit $testExit)." }
    Write-Host "Integration suite passed." -ForegroundColor Green
}
finally {
    Write-Host "Sweeping any leftover $RedisImage containers..." -ForegroundColor Cyan
    Remove-LeakedRedisContainers
}
