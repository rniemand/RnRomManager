#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs every test the CI runs (spec §16): dotnet test, then web lint, check and unit tests.
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

Push-Location $root
try {
    $testArgs = @('test')
    if ($SkipIntegration) { $testArgs += @('--filter', 'Category!=Integration') }
    if ($Coverage) {
        $coverageDir = Join-Path $root '.dev/coverage'
        $rawDir = Join-Path $coverageDir 'raw'
        if (Test-Path $coverageDir) { Remove-Item $coverageDir -Recurse -Force }
        $testArgs += @('--collect', 'XPlat Code Coverage', '--results-directory', $rawDir)
    }
    Invoke-Step 'dotnet test' { dotnet @testArgs }

    if ($Coverage) {
        Invoke-Step 'dotnet tool restore' { dotnet tool restore }
        Invoke-Step 'coverage report' {
            dotnet reportgenerator "-reports:$rawDir/*/coverage.cobertura.xml" "-targetdir:$coverageDir" '-reporttypes:Html'
        }
        Write-Host "Coverage report: $(Join-Path $coverageDir 'index.html')"
    }

    if (-not $SkipWeb) {
        Push-Location (Join-Path $root 'src/RnRomManager.Web')
        try {
            Invoke-Step 'npm run lint' { npm run lint }
            Invoke-Step 'npm run check' { npm run check }
            Invoke-Step 'npm test' { npm test }
        }
        finally {
            Pop-Location
        }
    }

    Write-Host 'All tests passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
