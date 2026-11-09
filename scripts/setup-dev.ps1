#!/usr/bin/env pwsh
<#
.SYNOPSIS
    One-off local setup (spec §16): tool checks, restore, npm ci, Playwright browsers, dev MariaDB, ./.dev folders and
    a git-ignored appsettings.Development.json with absolute paths. Safe to re-run.
.PARAMETER SkipDb
    Don't start the dev database (the connection string is still written).
#>
[CmdletBinding()]
param([switch]$SkipDb)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$webRoot = Join-Path $repoRoot 'src/RnRomManager.Web'
$e2eRoot = Join-Path $repoRoot 'tests/e2e'
$envFile = Join-Path $repoRoot '.env'
$settingsFile = Join-Path $repoRoot 'src/RnRomManager.Api/appsettings.Development.json'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Get-MajorVersion([string]$command, [string[]]$arguments) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { return $null }
    $output = @(& $command @arguments 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) { return $null }
    if (($output[0] -replace '^v', '') -notmatch '^(\d+)\.') { return $null }
    return [int]$Matches[1]
}

Write-Host '==> Checking tools'
$dotnet = Get-MajorVersion 'dotnet' @('--version')
if ($null -eq $dotnet -or $dotnet -lt 10) { throw '.NET 10 SDK is required (dotnet --version).' }
$node = Get-MajorVersion 'node' @('--version')
if ($null -eq $node -or $node -lt 24) { throw 'Node 24 or later is required (node --version).' }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker is required.' }
& docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is installed but not running.' }

Write-Host '==> dotnet tool restore + restore'
Invoke-Checked 'dotnet tool restore' { dotnet tool restore --tool-manifest (Join-Path $repoRoot '.config/dotnet-tools.json') }
Invoke-Checked 'dotnet restore' { dotnet restore (Join-Path $repoRoot 'RnRomManager.slnx') }

Write-Host '==> npm ci (web)'
Invoke-Checked 'npm ci' { npm ci --prefix $webRoot }

if (Test-Path -LiteralPath (Join-Path $e2eRoot 'package.json')) {
    Write-Host '==> Playwright browsers'
    Invoke-Checked 'npm ci (e2e)' { npm ci --prefix $e2eRoot }
    Push-Location $e2eRoot
    try { Invoke-Checked 'playwright install' { npx playwright install chromium } } finally { Pop-Location }
} else {
    Write-Host '==> Playwright browsers: skipped (no tests/e2e project yet)'
}

# The dev DB password lives only in the git-ignored .env (compose reads it) and appsettings.Development.json.
$password = $null
if (Test-Path -LiteralPath $envFile) {
    $line = Get-Content -LiteralPath $envFile | Where-Object { $_ -match '^\s*RNRM_DEV_DB_PASSWORD\s*=' } | Select-Object -First 1
    if ($line) { $password = ($line -split '=', 2)[1].Trim() }
}
if (-not $password) {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(18)
    $password = [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_')
    Add-Content -LiteralPath $envFile -Value "RNRM_DEV_DB_PASSWORD=$password"
    Write-Host "==> Wrote a new dev DB password to $envFile"
}

if (-not $SkipDb) {
    Write-Host '==> Starting the dev database'
    Invoke-Checked 'docker compose up' {
        docker compose -f (Join-Path $repoRoot 'docker-compose.dev.yml') --env-file $envFile up -d --wait
    }
}

Write-Host '==> Creating ./.dev folders'
$paths = [ordered]@{}
foreach ($name in 'roms', 'ingest', 'logs', 'cache', 'backups', 'collections') {
    $path = Join-Path $repoRoot ".dev/$name"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $paths[(Get-Culture).TextInfo.ToTitleCase($name)] = [IO.Path]::GetFullPath($path)
}

if (Test-Path -LiteralPath $settingsFile) {
    # Re-runs keep local edits; delete the file to regenerate it. Paths added since (e.g. Backups) are filled in,
    # otherwise the app would fall back to a Linux default that isn't absolute on Windows.
    Write-Host "==> Keeping the existing $settingsFile"
    $existing = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json -AsHashtable
    if (-not $existing.ContainsKey('Paths')) { $existing['Paths'] = [ordered]@{} }
    $added = @($paths.Keys | Where-Object { -not $existing['Paths'].ContainsKey($_) })
    if ($added.Count) {
        foreach ($key in $added) { $existing['Paths'][$key] = $paths[$key] }
        $existing | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsFile -Encoding utf8NoBOM
        Write-Host "==> Added Paths:$($added -join ', Paths:') to $settingsFile"
    }
} else {
    Write-Host "==> Writing $settingsFile"
    $settings = [ordered]@{
        ConnectionStrings = @{ Default = "Server=localhost;Port=3306;Database=rnrommanager;User ID=rnrommanager;Password=$password" }
        Paths             = $paths
    }
    $settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsFile -Encoding utf8NoBOM
}

Write-Host ''
Write-Host 'Done. Next: pwsh scripts/seed-ingest.ps1, then pwsh scripts/start-dev.ps1 and open http://localhost:5173.'
Write-Host 'The first API start prints the generated admin password to the console.'
