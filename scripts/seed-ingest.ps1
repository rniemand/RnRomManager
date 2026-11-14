#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seeds synthetic ROMs and matching fixture DATs for local ingestion (§15.2).
.PARAMETER Clean
    Empty the selected ingest folder before seeding. Cache and library contents are not removed.
.PARAMETER NdsDat
    ROM files the seeded Nintendo DS No-Intro DAT lists instead of the empty catalog (game name: the file name without its
    extension; ROM name: the file name). The files are not copied into ingest.
.PARAMETER NdsGenerate
    Instead of seeding, write one synthetic DS ROM with a valid header CRC to this path (streamed), using -Size and -Seed.
.EXAMPLE
    pwsh scripts/seed-ingest.ps1 -NdsGenerate big-a.nds -Size 536870912 -Seed 1
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $PSScriptRoot '../.dev/ingest'),
    [string]$CachePath = (Join-Path $PSScriptRoot '../.dev/cache'),
    [ValidateSet('Basic', 'Full')][string]$Set = 'Basic',
    [switch]$Clean,
    [string[]]$NdsDat = @(),
    [string]$NdsGenerate,
    [long]$Size,
    [int]$Seed
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'tests/RnRomManager.TestData'
if ($NdsGenerate) {
    if (-not $PSBoundParameters.ContainsKey('Size') -or -not $PSBoundParameters.ContainsKey('Seed')) { throw '-NdsGenerate needs -Size and -Seed.' }
    $target = [IO.Path]::GetFullPath($NdsGenerate, $PWD.ProviderPath)
    & dotnet run --project $project -- --nds-generate $target --size $Size --seed $Seed
    if ($LASTEXITCODE -ne 0) { throw "TestData CLI failed (exit $LASTEXITCODE)" }
    return
}
$ingestRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path, $PWD.ProviderPath))
$cacheRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($CachePath, $PWD.ProviderPath))
$comparison = [StringComparison]::OrdinalIgnoreCase
$separator = [IO.Path]::DirectorySeparatorChar
$canonicalSet = if ($Set -ieq 'Basic') { 'Basic' } else { 'Full' }
if ($ingestRoot -eq [IO.Path]::GetPathRoot($ingestRoot) -or $cacheRoot -eq [IO.Path]::GetPathRoot($cacheRoot)) {
    throw 'Ingest and cache must not be filesystem roots.'
}
if ($ingestRoot.Equals($cacheRoot, $comparison) -or $ingestRoot.StartsWith($cacheRoot + $separator, $comparison) -or $cacheRoot.StartsWith($ingestRoot + $separator, $comparison)) {
    throw 'Ingest and cache must be distinct, non-nested folders.'
}
if ($Clean) {
    if ($repoRoot.Equals($ingestRoot, $comparison) -or $repoRoot.StartsWith($ingestRoot + $separator, $comparison)) {
        throw 'Cannot clean the repository or an ancestor directory.'
    }
    if (Test-Path -LiteralPath $ingestRoot) {
        $rootItem = Get-Item -LiteralPath $ingestRoot -Force
        $children = @(Get-ChildItem -LiteralPath $ingestRoot -Force -Recurse)
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) {
            throw 'Cannot clean a folder containing symbolic links or junctions.'
        }
        # Resolve and check every deletion target before mutating anything.
        $targets = @(Get-ChildItem -LiteralPath $ingestRoot -Force | ForEach-Object { [IO.Path]::GetFullPath($_.FullName) })
        foreach ($target in $targets) {
            if (-not $target.StartsWith($ingestRoot + $separator, $comparison)) { throw "Clean target is outside ingest: $target" }
        }
        foreach ($target in $targets) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
$ndsArgs = @($NdsDat | ForEach-Object { '--nds-dat'; [IO.Path]::GetFullPath($_, $PWD.ProviderPath) })
& dotnet run --project $project -- --ingest $ingestRoot --cache $cacheRoot --set $canonicalSet @ndsArgs
if ($LASTEXITCODE -ne 0) { throw "TestData CLI failed (exit $LASTEXITCODE)" }
