#!/usr/bin/env pwsh
# =============================================================================
# End-to-end lifecycle demo against the REAL hardened container, as a black box.
#
#   pwsh scripts/e2e-test.ps1
#
# Builds the hardened image, then runs tests/GameServer.EndToEndTests. The test
# itself starts the image via Testcontainers and maps :8080 to a RANDOM host port
# (so it sidesteps the Windows+Podman published-port relay that wedges a fixed
# `-p 8080:8080`). It then drives the full lifecycle over real HTTP + WebSocket:
# list rooms (always >=1) -> client #1 connects + sees state -> client #2 joins
# the same room -> client #1 submits MoveRight -> both clients observe x == 1.
#
# Requires a reachable container engine (Podman). With none, the test SKIPS green.
# Testcontainers is pointed at Podman via ~/.testcontainers.properties
# (docker.host=npipe://./pipe/podman-machine-default, ryuk.disabled=true).
# =============================================================================
[CmdletBinding()]
param([string]$Tag = 'citadel-host:local')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# 1) Build the image the test runs (the test does NOT build it).
& (Join-Path $PSScriptRoot 'podman-build.ps1') -Tag $Tag
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2) Run the e2e suite. Testcontainers owns the container lifecycle + port mapping;
#    there is deliberately no `podman run -p` here.
Write-Host "Running end-to-end lifecycle suite..." -ForegroundColor Cyan
dotnet test (Join-Path $repoRoot 'tests/GameServer.EndToEndTests') --nologo
exit $LASTEXITCODE
