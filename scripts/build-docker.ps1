#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the RnRomManager image into the local Docker engine (spec §16).
.PARAMETER Tag
    Image tag for niemandr/rnrommanager. Default: dev.
.PARAMETER Platform
    e.g. linux/amd64 or linux/arm64. Default: the local platform.
.PARAMETER Version
    App version baked into the build. Default: `dotnet minver --tag-prefix v`.
#>
[CmdletBinding()]
param(
    [string]$Tag = 'dev',
    [string]$Platform,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $root
try {
    if (-not $Version) {
        dotnet tool restore | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)" }
        $Version = (dotnet minver --tag-prefix v --verbosity error | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $Version) { throw "dotnet minver failed ($LASTEXITCODE)" }
    }

    $image = "niemandr/rnrommanager:$Tag"
    $dockerArgs = @('buildx', 'build', '--load', '-t', $image, '--build-arg', "VERSION=$Version")
    if ($Platform) { $dockerArgs += @('--platform', $Platform) }
    $dockerArgs += '.'

    Write-Host "Building $image (version $Version)"
    & docker @dockerArgs
    if ($LASTEXITCODE -ne 0) { throw "docker build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}
