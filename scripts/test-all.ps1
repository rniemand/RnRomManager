#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the .NET test shards and web lint, type checks and unit tests concurrently.
.PARAMETER SkipIntegration
    Skip Testcontainers tests ([Category("Integration")]), so Docker isn't needed.
.PARAMETER SkipWeb
    Skip the web app checks.
.PARAMETER Coverage
    Collect coverage and write an HTML report to ./.dev/coverage.
#>
[CmdletBinding()]
param(
    [switch]$SkipIntegration,
    [switch]$SkipWeb,
    [switch]$Coverage
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)" }
}

$webJob = $null
Push-Location $root
try {
    # Builds once, then runs the test shards in parallel (see test-dotnet.ps1).
    $testArgs = @{}
    if ($SkipIntegration) { $testArgs.SkipIntegration = $true }
    if ($Coverage) {
        $coverageDir = Join-Path $root '.dev/coverage'
        $rawDir = Join-Path $coverageDir 'raw'
        if (Test-Path $coverageDir) { Remove-Item $coverageDir -Recurse -Force }
        $testArgs.Coverage = $true
        $testArgs.ResultsDirectory = $rawDir
    }
    if (-not $SkipWeb) {
        $webRoot = Join-Path $root 'src/RnRomManager.Web'
        $webJob = Start-Job -WorkingDirectory $webRoot -ScriptBlock {
            $ErrorActionPreference = 'Stop'
            foreach ($arguments in @(@('run', 'lint'), @('run', 'check'), @('test'))) {
                Write-Host "==> npm $arguments" -ForegroundColor Cyan
                & npm @arguments
                if ($LASTEXITCODE -ne 0) { throw "npm $arguments failed (exit $LASTEXITCODE)" }
            }
        }
    }

    Invoke-Step 'codemap check' { & (Join-Path $PSScriptRoot 'codemap.ps1') -Check }

    Invoke-Step 'dotnet test' { & (Join-Path $PSScriptRoot 'test-dotnet.ps1') @testArgs }

    if ($Coverage) {
        Invoke-Step 'dotnet tool restore' { dotnet tool restore }
        Invoke-Step 'coverage report' {
            dotnet reportgenerator "-reports:$rawDir/*/coverage.cobertura.xml" "-targetdir:$coverageDir" '-reporttypes:Html'
        }
        Write-Host "Coverage report: $(Join-Path $coverageDir 'index.html')"
    }

    if ($webJob) {
        Receive-Job -Job $webJob -Wait
        if ($webJob.State -ne 'Completed') { throw "Web checks ended in state $($webJob.State)" }
    }

    Write-Host 'All tests passed.' -ForegroundColor Green
}
finally {
    if ($webJob) { Remove-Job -Job $webJob -Force -ErrorAction SilentlyContinue }
    Pop-Location
}
