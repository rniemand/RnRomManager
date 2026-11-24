#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the .NET tests split into shards: separate `dotnet test` processes, each with its own MariaDB container.
.DESCRIPTION
    The API host's logging (NLog) is process-wide, so API tests can't run in parallel inside one process; this
    script splits them across processes instead. The first shards are named API test classes, balanced by run time,
    then whole test projects that get a shard of their own (Data.Tests); the last shard is everything else (the rest
    of the API tests and every other test project), so a new test class always runs somewhere. CI runs one shard per job (.github/workflows/ci.yml); locally, -Shard 0 runs all of them
    at once.
.PARAMETER Shard
    1..N runs that shard; 0 (default) builds once and runs every shard in parallel.
.PARAMETER ExpectedShards
    Fail unless the script defines exactly this many shards (CI passes its matrix size, so none is skipped).
.PARAMETER Configuration
    Build configuration (default Debug).
.PARAMETER NoBuild
    Don't build first (the build must already exist).
.PARAMETER SkipIntegration
    Skip Testcontainers tests ([Category("Integration")]), so Docker isn't needed.
.PARAMETER Coverage
    Collect XPlat Code Coverage into -ResultsDirectory.
.PARAMETER ResultsDirectory
    Where test results (and coverage) go. Default: ./.dev/test-results.
#>
[CmdletBinding()]
param(
    [int]$Shard = 0,
    [int]$ExpectedShards = 0,
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$SkipIntegration,
    [switch]$Coverage,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $root '.dev/test-results' }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory, $root)
$apiTests = Join-Path $root 'tests/RnRomManager.Api.Tests'
$solution = Join-Path $root 'RnRomManager.slnx'

# API test classes per shard, balanced on CI (ubuntu-latest) class durations from the shards' TRX files: each class
# counted from the previous class's last test to its own, so fixture setup is included. Local Windows timings differ a
# lot and aren't a guide. An API shard only builds the API tests (~60 s on CI), the last shard builds the whole
# solution (~95 s), so the last shard should carry the least test time. It also runs the other test projects in
# parallel processes and any API classes unlisted here; on a 4-vCPU runner that contention makes API tests there 2-3x
# slower, so only small classes stay unlisted.
$apiShards = @(
    @('IngestPipelineTests'),
    @('AuditWriterTests', 'HealthReadyTests', 'JobEndpointsTests', 'DiscoveryEndpointsTests', 'ResetPasswordCommandTests',
        'CollectionEndpointsTests', 'ExternalZipReadTests', 'IngestDeleteExternalEndpointsTests', 'SecurityHeadersTests',
        'IntegrityEndpointsTests', 'ImageUrlEndpointsTests', 'IngestEndpointsTests', 'AdminSeedTests', 'ZipEntryEndpointsTests',
        'StorageMigrationJobTests', 'ZipCompressionEndpointsTests', 'RomStorageEndpointsTests'),
    @('BackupEndpointsTests', 'DataProtectionKeysTests', 'AuditEndpointsTests', 'ExternalLibraryTests', 'EmulatorEndpointsTests',
        'ReportEndpointsTests', 'DatEndpointsTests', 'LegacyStorageTests', 'AuditLibraryEditTests', 'IngestReviewTests',
        'EndpointRoleMatrixTests', 'ImageEndpointsTests', 'BundleQueueEndpointsTests', 'TitleDbIngestTests', 'NoteEndpointsTests'),
    @('BundleDownloadEndpointsTests', 'AppHostTests', 'LibraryEndpointsTests', 'SeedIngestTests', 'LibraryEditTests',
        'IngestArchiveTests', 'LibraryEditorSaveTests', 'LibraryMoveMergeTests', 'BundleFileEndpointsTests',
        'IngestExternalArchiveTests'),
    @('AdminEndpointsTests', 'AuthTests', 'ReviewEndpointsTests', 'SaveEndpointsTests', 'IngestRestoreEndpointsTests',
        'LogStreamTests', 'IngestDeleteEndpointsTests', 'HotkeyEndpointsTests', 'CollectionCoverEndpointsTests',
        'MediaRetryEndpointsTests', 'ActivityReportEndpointTests', 'SaveCarryTests', 'ReleaseEditTests',
        'DiscoveryLogoEndpointsTests', 'ImageSuggestionEndpointsTests', 'GamePlayedEndpointTests', 'UserEndpointsTests',
        'GameInfoLookupEndpointsTests')
)
# Whole test projects that get a shard of their own, after the API shards. Data.Tests (~75 s of MariaDB tests) made
# the last shard the slowest by far while it ran there alongside the leftover API classes.
$projectShards = @('tests/RnRomManager.Data.Tests')
$shardCount = $apiShards.Count + $projectShards.Count + 1

function Get-ShardArgs([int]$n) {
    # `.Name.` matches exactly that class (NUnit's FullyQualifiedName is Namespace.Class.Method(args)).
    $filter = $null
    if ($n -le $apiShards.Count) {
        $target = $apiTests
        $filter = '(' + (($apiShards[$n - 1] | ForEach-Object { "FullyQualifiedName~.$_." }) -join '|') + ')'
    } elseif ($n -lt $shardCount) {
        $target = Join-Path $root $projectShards[$n - $apiShards.Count - 1]
    } else {
        $target = $solution
        $filter = (@($apiShards | ForEach-Object { $_ } | ForEach-Object { "FullyQualifiedName!~.$_." }) +
            @($projectShards | ForEach-Object { "FullyQualifiedName!~$(Split-Path $_ -Leaf)." })) -join '&'
    }
    if ($SkipIntegration) { $filter = if ($filter) { "($filter)&Category!=Integration" } else { 'Category!=Integration' } }
    $a = @('test', $target, '-c', $Configuration, '--no-build', '--results-directory', $ResultsDirectory,
        # Per-test durations, for balancing $apiShards (CI uploads them with the coverage).
        '--logger', 'trx')
    if ($filter) { $a += @('--filter', $filter) }
    if ($Coverage) { $a += @('--collect', 'XPlat Code Coverage') }
    return , $a
}

if ($ExpectedShards -and $ExpectedShards -ne $shardCount) {
    throw "test-dotnet.ps1 defines $shardCount shards but -ExpectedShards is $ExpectedShards; update the CI matrix."
}
if ($Shard -lt 0 -or $Shard -gt $shardCount) { throw "-Shard must be 0..$shardCount." }

if (-not $NoBuild) {
    $buildTarget = if ($Shard -gt 0 -and $Shard -lt $shardCount) { (Get-ShardArgs $Shard)[1] } else { $solution }
    Write-Host "==> dotnet build ($Configuration)" -ForegroundColor Cyan
    & dotnet build $buildTarget -c $Configuration -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}

if ($Shard -gt 0) {
    $dotnetArgs = Get-ShardArgs $Shard
    Write-Host "==> dotnet test, shard $Shard of $shardCount" -ForegroundColor Cyan
    & dotnet @dotnetArgs
    exit $LASTEXITCODE
}

# Every shard at once; each writes to its own log, shown when it finishes.
$logDir = Join-Path $ResultsDirectory 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$runs = 1..$shardCount | ForEach-Object { [pscustomobject]@{ Shard = $_; Args = (Get-ShardArgs $_); Log = Join-Path $logDir "shard-$_.log" } }
Write-Host "==> dotnet test: $shardCount shards in parallel (logs in $logDir)" -ForegroundColor Cyan
$watch = [Diagnostics.Stopwatch]::StartNew()
$results = $runs | ForEach-Object -ThrottleLimit $shardCount -Parallel {
    $run = $_
    $start = [Diagnostics.Stopwatch]::StartNew()
    $dotnetArgs = $run.Args
    & dotnet @dotnetArgs *> $run.Log
    [pscustomobject]@{ Shard = $run.Shard; ExitCode = $LASTEXITCODE; Log = $run.Log; Seconds = [int]$start.Elapsed.TotalSeconds }
}

$failed = @()
foreach ($r in $results | Sort-Object Shard) {
    $colour = if ($r.ExitCode -eq 0) { 'Green' } else { 'Red' }
    Write-Host "--- shard $($r.Shard): exit $($r.ExitCode) in $($r.Seconds) s" -ForegroundColor $colour
    Get-Content $r.Log | Where-Object { $_ -match '^\s*(Passed!|Failed!|Failed |Skipped!)' -or ($r.ExitCode -ne 0 -and $_ -match 'error') }
    if ($r.ExitCode -ne 0) { $failed += $r }
}
Write-Host "==> all shards done in $([int]$watch.Elapsed.TotalSeconds) s"
if ($failed) {
    foreach ($r in $failed) {
        Write-Host "==> shard $($r.Shard) log tail ($($r.Log))" -ForegroundColor Red
        Get-Content $r.Log -Tail 80
    }
    exit 1
}
exit 0
