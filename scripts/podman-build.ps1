#!/usr/bin/env pwsh
# Builds the hardened Citadel host image with Podman.
#
#   pwsh scripts/podman-build.ps1
#
# Idempotent: re-running rebuilds the image (layer cache makes it fast). Fails
# with a clear message if podman is not installed.
[CmdletBinding()]
param(
    [string]$Tag = 'citadel-host:local'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Error "podman is not on PATH. Install Podman (https://podman.io) and retry."
    exit 1
}

# Resolve the repo root from this script's location so the build works from any CWD.
$repoRoot = Split-Path -Parent $PSScriptRoot
$containerfile = Join-Path $repoRoot 'Containerfile'

if (-not (Test-Path $containerfile)) {
    Write-Error "Containerfile not found at $containerfile."
    exit 1
}

Write-Host "Building $Tag from $containerfile (context: $repoRoot)..." -ForegroundColor Cyan
podman build -t $Tag -f $containerfile $repoRoot
if ($LASTEXITCODE -ne 0) {
    Write-Error "podman build failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host "Built $Tag." -ForegroundColor Green
