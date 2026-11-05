#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the API (dotnet watch, :5080) and the Vite dev server (proxies /api and /health to the API). Ctrl+C stops both.
.PARAMETER ApiOnly
    Only the API.
.PARAMETER WebOnly
    Only the Vite dev server (against an API you run yourself on :5080).
#>
[CmdletBinding()]
param([switch]$ApiOnly, [switch]$WebOnly)
$ErrorActionPreference = 'Stop'
if ($ApiOnly -and $WebOnly) { throw 'Use -ApiOnly or -WebOnly, not both.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$apiProject = Join-Path $repoRoot 'src/RnRomManager.Api'
$webRoot = Join-Path $repoRoot 'src/RnRomManager.Web'

if (-not $WebOnly -and -not (Test-Path -LiteralPath (Join-Path $apiProject 'appsettings.Development.json'))) {
    throw 'appsettings.Development.json is missing: run pwsh scripts/setup-dev.ps1 first.'
}

$apiArgs = @('watch', '--project', $apiProject, 'run', '--launch-profile', 'http')

if ($ApiOnly) {
    & dotnet @apiArgs
    exit $LASTEXITCODE
}
if ($WebOnly) {
    & npm run dev --prefix $webRoot
    exit $LASTEXITCODE
}

# The API runs as a child process sharing this console; Vite runs in the foreground. Ctrl+C ends Vite, and the
# finally block stops the API's whole process tree (dotnet watch → the app).
$api = Start-Process -FilePath 'dotnet' -ArgumentList $apiArgs -NoNewWindow -PassThru
try {
    & npm run dev --prefix $webRoot
} finally {
    if (-not $api.HasExited) { $api.Kill($true) }
}
