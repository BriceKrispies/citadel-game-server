#!/usr/bin/env pwsh
# =============================================================================
# Black-box security harness for the Citadel host container.
#
#   pwsh scripts/security-test.ps1
#
# Builds the hardened image, boots it under a locked-down runtime profile
# (--read-only, --cap-drop=ALL, --tmpfs /tmp), injects a strong join-token
# secret + control-plane API keys (tenant-a, tenant-b, platform-admin), waits
# for /ready, then runs the black-box security suite against it. The container
# is ALWAYS torn down in the finally block.
#
# ## TLS trust boundary
#   The container serves PLAINTEXT HTTP on :8080. In production TLS terminates at
#   the AWS ECS ALB and decrypted HTTP is forwarded to the task. This harness
#   therefore tests over http:// on purpose — it exercises the same plaintext
#   listener the ALB targets.
# =============================================================================
[CmdletBinding()]
param(
    [string]$Tag = 'citadel-host:local',
    [int]$Port = 8080,
    [int]$ReadyTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Error "podman is not on PATH. Install Podman and retry."
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$containerName = 'citadel-sec-test'

# --- Test credentials injected at runtime (NOT baked into the image) ---------
# A 40+ char secret satisfies the >=32 fail-closed bar in Program.cs.
$joinSecret = 'citadel-security-test-join-secret-0123456789'
$tenantAKey = 'sec-test-tenant-a-key-0001'
$tenantBKey = 'sec-test-tenant-b-key-0002'
$adminKey   = 'sec-test-platform-admin-key-0003'

$target = "http://localhost:$Port"

# 1) Build the image.
& (Join-Path $PSScriptRoot 'podman-build.ps1') -Tag $Tag
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Make sure no stale container with our name is around.
podman rm -f $containerName *> $null

try {
    Write-Host "Starting container $containerName (locked-down profile)..." -ForegroundColor Cyan
    podman run -d --name $containerName `
        --read-only `
        --tmpfs /tmp `
        --cap-drop=ALL `
        -p "${Port}:8080" `
        -e "Auth__JoinTokenSecret=$joinSecret" `
        -e "ControlPlane__ApiKeys__0__Key=$tenantAKey" `
        -e "ControlPlane__ApiKeys__0__CallerId=sec-operator-a" `
        -e "ControlPlane__ApiKeys__0__TenantId=tenant-a" `
        -e "ControlPlane__ApiKeys__1__Key=$tenantBKey" `
        -e "ControlPlane__ApiKeys__1__CallerId=sec-operator-b" `
        -e "ControlPlane__ApiKeys__1__TenantId=tenant-b" `
        -e "ControlPlane__ApiKeys__2__Key=$adminKey" `
        -e "ControlPlane__ApiKeys__2__CallerId=sec-platform-admin" `
        -e "ControlPlane__ApiKeys__2__TenantId=tenant-a" `
        -e "ControlPlane__ApiKeys__2__Roles__0=platform-admin" `
        $Tag
    if ($LASTEXITCODE -ne 0) { throw "podman run failed (exit $LASTEXITCODE)." }

    # 2) Poll /ready until green (bounded).
    Write-Host "Waiting up to ${ReadyTimeoutSeconds}s for $target/ready ..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-WebRequest -Uri "$target/ready" -TimeoutSec 3 -SkipHttpErrorCheck
            if ($resp.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            # not up yet
        }
        Start-Sleep -Milliseconds 750
    }

    if (-not $ready) {
        Write-Host "----- container logs -----" -ForegroundColor Yellow
        podman logs $containerName
        throw "Container never became ready within ${ReadyTimeoutSeconds}s."
    }
    Write-Host "/ready is green." -ForegroundColor Green

    # 3) Export the target + credentials for the test suite to consume.
    $env:CITADEL_SECURITY_TARGET = $target
    $env:CITADEL_TEST_APIKEY     = $tenantAKey
    $env:CITADEL_TEST_APIKEY_B   = $tenantBKey
    $env:CITADEL_TEST_ADMINKEY   = $adminKey
    $env:CITADEL_TEST_JOINSECRET = $joinSecret

    # 4) Optional image scan (warn-only).
    if (Get-Command trivy -ErrorAction SilentlyContinue) {
        Write-Host "Running trivy image scan (warn-only)..." -ForegroundColor Cyan
        trivy image $Tag
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "trivy reported findings (exit $LASTEXITCODE) — not failing the run."
        }
    } else {
        Write-Host "trivy not on PATH; skipping image scan." -ForegroundColor DarkGray
    }

    # 5) Run the black-box security suite (incl. DoS category).
    #    The project is delivered in ticket 002. Until then, detect-and-warn so this
    #    harness can still be exercised end-to-end, but ALWAYS keep the invocation.
    $secProj = Join-Path $repoRoot 'tests/GameServer.SecurityTests'
    if (-not (Test-Path $secProj)) {
        Write-Warning "tests/GameServer.SecurityTests does not exist yet (ticket 002). Skipping the suite run; container boot + ready + posture were validated."
    } else {
        Write-Host "Running security suite against $target ..." -ForegroundColor Cyan
        dotnet test $secProj
        if ($LASTEXITCODE -ne 0) { throw "Security test suite failed (exit $LASTEXITCODE)." }
        Write-Host "Security suite passed." -ForegroundColor Green
    }
}
finally {
    Write-Host "Tearing down container $containerName ..." -ForegroundColor Cyan
    podman rm -f $containerName *> $null
}
