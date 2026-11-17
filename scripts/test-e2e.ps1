#!/usr/bin/env pwsh
<#
.SYNOPSIS
    End-to-end tests (spec §15.3): builds the image for the local arch, seeds Basic into temp ingest/cache dirs, starts
    tests/e2e/docker-compose.e2e.yml, waits for /health/ready, runs Playwright, and always tears down. The specs run on
    two stacks at once (ports E2E_PORT and E2E_PORT + 1), split by tag (see tests/e2e/specs/mvp.spec.ts).
.PARAMETER Shard
    All (default) runs both halves on two stacks; A or B runs one half on one stack (CI runs each in its own job).
.PARAMETER SkipBuild
    Use the existing niemandr/rnrommanager:e2e image.
.PARAMETER Headed
    Show the browser.
.PARAMETER Screenshots
    Regenerate the README screenshots with the screenshot project.
#>
[CmdletBinding()]
param([ValidateSet('All', 'A', 'B')][string]$Shard = 'All', [switch]$SkipBuild, [switch]$Headed, [switch]$Screenshots)
$twoStacks = $Shard -eq 'All' -and -not $Screenshots
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$e2eRoot = Join-Path $repoRoot 'tests/e2e'
$composeFile = Join-Path $e2eRoot 'docker-compose.e2e.yml'
$port = if ($env:E2E_PORT) { $env:E2E_PORT } else { '18080' }

function New-Secret {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(18)
    return [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_')
}

if (-not $SkipBuild) {
    # Match the CI e2e image version so version changes alone don't invalidate the publish layer.
    & (Join-Path $PSScriptRoot 'build-docker.ps1') -Tag e2e -Version '0.0.0-dev'
}

$work = Join-Path ([IO.Path]::GetTempPath()) "rnrm-e2e-$([Guid]::NewGuid().ToString('N'))"
$env:E2E_INGEST = Join-Path $work 'ingest'
$env:E2E_CACHE = Join-Path $work 'cache'
$env:E2E_PORT = $port
# Fresh per run, never written to disk or printed.
$env:E2E_DB_PASSWORD = New-Secret
$env:E2E_ADMIN_PASSWORD = New-Secret
$env:E2E_BASE_URL = "http://localhost:$port"
# Stack A runs the screenshots or the untagged and @both specs; stack B (two-stack runs only) the @both and @shard-b ones.
$stacks = @([pscustomobject]@{ Project = 'rnrommanager-e2e'; Port = $port; Ingest = $env:E2E_INGEST; Cache = $env:E2E_CACHE })
if ($twoStacks) {
    $portB = [string]([int]$port + 1)
    $env:E2E_INGEST_B = Join-Path $work 'ingest-b'
    $cacheB = Join-Path $work 'cache-b'
    $env:E2E_BASE_URL_B = "http://localhost:$portB"
    $stacks += [pscustomobject]@{ Project = 'rnrommanager-e2e-b'; Port = $portB; Ingest = $env:E2E_INGEST_B; Cache = $cacheB }
} else {
    Remove-Item Env:E2E_BASE_URL_B, Env:E2E_INGEST_B -ErrorAction SilentlyContinue
}

$exitCode = 1
try {
    # Nintendo DS (spec Step 10): the runnable test cartridge waits outside ingest/ until mvp.spec.ts copies it in, after
    # the count-sensitive tests; the seeded DS DAT lists it (one FixtureDatWriter pass, one manifest). The screenshots run
    # seeds the empty DS catalog and doesn't run mvp.spec.ts.
    $seedArgs = @{ Path = $env:E2E_INGEST; CachePath = $env:E2E_CACHE; Set = 'Basic' }
    if (-not $Screenshots) {
        $staged = Join-Path $work 'staged'
        New-Item -ItemType Directory -Force $staged | Out-Null
        $env:E2E_STAGED_NDS = Join-Path $staged 'Marker Test (World).nds'
        Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/RnRomManager.TestData/Fixtures/nds-marker/nds-marker.nds') -Destination $env:E2E_STAGED_NDS
        $seedArgs.NdsDat = @($env:E2E_STAGED_NDS)
    }
    & (Join-Path $PSScriptRoot 'seed-ingest.ps1') @seedArgs
    if ($twoStacks) {
        # Stack B gets its own identical seed; it shares the passwords and the image.
        $seedB = $seedArgs.Clone()
        $seedB.Path = $env:E2E_INGEST_B
        $seedB.CachePath = $cacheB
        & (Join-Path $PSScriptRoot 'seed-ingest.ps1') @seedB
    }
    # The container runs as UID 1654; a Linux bind mount needs to be writable by it (Docker Desktop maps this itself).
    if ($IsLinux) {
        & chmod -R a+rwX $work
        if ($LASTEXITCODE -ne 0) { throw "chmod failed ($LASTEXITCODE)" }
    }

    foreach ($stack in $stacks) {
        Write-Host "==> Starting e2e stack $($stack.Project) on port $($stack.Port)"
        $env:E2E_PORT = $stack.Port
        $env:E2E_INGEST = $stack.Ingest
        $env:E2E_CACHE = $stack.Cache
        & docker compose -p $stack.Project -f $composeFile up -d
        if ($LASTEXITCODE -ne 0) { throw "docker compose up failed ($LASTEXITCODE)" }
    }
    # Back to stack A for the specs (the Nintendo DS test copies into its ingest folder).
    $env:E2E_PORT = $port
    $env:E2E_INGEST = $stacks[0].Ingest
    $env:E2E_CACHE = $stacks[0].Cache

    foreach ($stack in $stacks) {
        $url = "http://localhost:$($stack.Port)"
        Write-Host "==> Waiting for $url/health/ready"
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        $ready = $false
        while (-not $ready -and [DateTime]::UtcNow -lt $deadline) {
            try {
                $ready = (Invoke-WebRequest "$url/health/ready" -TimeoutSec 3 -SkipHttpErrorCheck).StatusCode -eq 200
            } catch { }
            if (-not $ready) { Start-Sleep -Seconds 2 }
        }
        if (-not $ready) { throw "$url did not become ready within 120 s." }
    }

    Write-Host '==> Playwright'
    Push-Location $e2eRoot
    try {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)" }
        $install = @('playwright', 'install', 'chromium')
        if ($IsLinux -and $env:CI) { $install += '--with-deps' }
        & npx @install
        if ($LASTEXITCODE -ne 0) { throw "playwright install failed ($LASTEXITCODE)" }
        $testArgs = @('playwright', 'test')
        if ($Screenshots) {
            $env:E2E_SCREENSHOTS = '1'
            $testArgs += '--project=screenshots'
        } elseif ($twoStacks) {
            $testArgs += @('--project=chromium', '--project=chromium-b')
        } else {
            # One half on the one stack: the same tag split as chromium / chromium-b in playwright.config.ts.
            $testArgs += '--project=chromium'
            $testArgs += if ($Shard -eq 'A') { @('--grep-invert', '@shard-b') } else { @('--grep', '@both|@shard-b') }
        }
        if ($Headed) { $testArgs += '--headed' }
        & npx @testArgs
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        foreach ($stack in $stacks) {
            Write-Host "==> App log of $($stack.Project) (last 200 lines)"
            & docker compose -p $stack.Project -f $composeFile logs --no-color --tail 200 app
        }
    }
} finally {
    Write-Host '==> Tearing down'
    foreach ($stack in $stacks) { & docker compose -p $stack.Project -f $composeFile down -v --remove-orphans }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedWork = [IO.Path]::GetFullPath($work)
    if ([IO.Path]::GetDirectoryName($resolvedWork) -eq [IO.Path]::TrimEndingDirectorySeparator($tempRoot) -and
        [IO.Path]::GetFileName($resolvedWork) -match '^rnrm-e2e-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
