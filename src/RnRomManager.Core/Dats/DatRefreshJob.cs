using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Dats;

/// <summary>
/// "Refresh DATs" (spec §10.1, D43, T2): downloads every system's 5 files into <c>cache/work/&lt;jobId&gt;/dats/</c>,
/// parses each, writes <c>dats.json</c> there and builds every store from that staged set exactly as startup would.
/// Only then is the folder swapped in for <c>cache/dats/</c> and the already-built stores published. Any failure
/// (HTTP, parse, wrong DAT, staged validation) or a cancel before the first rename keeps the old files and stores.
/// Cancel is checked between downloads and once more right before the swap. A <c>TitleDb</c> system (T2) instead
/// shares one download of <c>Sources:TitleDbUrl</c>, streamed to <c>cache/work/&lt;jobId&gt;/titledb.json</c> and
/// parsed from there once, and gets its own compact file.
/// </summary>
public sealed class DatRefreshJob(
    HttpClient http, SourcesOptions sources, PathsOptions paths, DatCache cache, ISystemRegistry systems, IAuditLog audit,
    TimeProvider time, ILogger<DatRefreshJob> logger) : IJob
{
    /// <summary>The largest DAT is ~1 MiB; anything far bigger is not a DAT.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>T2: the titledb source was 90,699,593 bytes on 2026-11-10.</summary>
    public const long MaxTitleDbBytes = 256L * 1024 * 1024;

    /// <summary>The downloaded titledb source, inside the job's work folder.</summary>
    public const string TitleDbSourceFile = "titledb.json";

    /// <summary>The titledb cap; settable so tests needn't send 256 MiB.</summary>
    public long TitleDbByteLimit { get; set; } = MaxTitleDbBytes;

    /// <summary>Test seams: the directory renames and deletes of publication.</summary>
    public DatPublicationHooks? PublicationHooks { get; set; }

    public JobType Type => JobType.DatRefresh;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        CheckPlan(systems.All);
        var titleDb = systems.All.Where(h => h.DatSource == DatSource.TitleDb).ToList();
        var files = systems.All.Where(h => h.DatSource == DatSource.Libretro)
            .SelectMany(h => DatKind.All.Select(kind => (Handler: h, Kind: kind))).ToList();
        // One download per libretro file, plus one shared titledb download.
        context.Progress.SetTotal(files.Count + (titleDb.Count > 0 ? 1 : 0));

        var work = CreateWork(context.JobId);
        var staged = Path.Combine(work, DatCache.FolderName);
        try
        {
            var manifest = new List<DatManifestEntry>();
            foreach (var (handler, kind) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = DatKind.RelativePath(kind, handler.DatName);
                context.Progress.SetCurrentItem(relative);

                var bytes = await DownloadAsync($"{sources.DatBaseUrl.TrimEnd('/')}/{kind}/{Uri.EscapeDataString(handler.DatName)}.dat", cancellationToken)
                    .ConfigureAwait(false);
                var (datName, version) = Parse(Encoding.UTF8.GetString(bytes), kind, handler, relative);

                var target = SafePath.Resolve(staged, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                manifest.Add(new DatManifestEntry(relative, datName, version, time.GetUtcNow(), Convert.ToHexStringLower(SHA1.HashData(bytes))));
                context.Progress.ItemDone();
            }

            if (titleDb.Count > 0)
                manifest.AddRange(await StageTitleDbAsync(titleDb, work, staged, context, cancellationToken).ConfigureAwait(false));

            cancellationToken.ThrowIfCancellationRequested();
            DatManifest.Write(Path.Combine(staged, DatManifest.FileName), manifest);
            var stores = cache.Prepare(staged);
            // T3: the result lists each no-intro DAT and each titledb file by DatName.
            var versions = manifest.Where(e => e.File.StartsWith(DatKind.NoIntro + "/", StringComparison.Ordinal)
                    || e.File.StartsWith(TitleDb.Folder + "/", StringComparison.Ordinal))
                .ToDictionary(e => e.DatName, e => e.Version);

            // The last point a cancel keeps the old set: from the first rename on, publication runs to the end.
            cancellationToken.ThrowIfCancellationRequested();
            DatPublication.Swap(paths.Cache, staged, logger, PublicationHooks);

            // Committed: no token from here on. A cancel now still ends the job Cancelled, with the new set active.
            cache.Publish(stores);
            // AU4: every successful run replaced cache/dats.
            audit.Record(new AuditEvent(AuditActions.DatsRefreshed, context.Actor,
                Details: string.Join(", ", manifest.Select(e => e.DatName).Distinct(StringComparer.Ordinal))));
            DatPublication.RemoveBackup(paths.Cache, logger, PublicationHooks);
            logger.LogInformation("DATs refreshed: {Versions}", versions);
            return new { files = manifest.Count, versions };
        }
        finally
        {
            // Best effort: only the folder this run created, while it is still a real directory.
            try
            {
                var owned = ExternalRomPath.ClassifyDirectory(paths.Cache, work);
                if (owned.Status == ExternalPathStatus.Found)
                    (PublicationHooks ?? new()).Delete(owned.Path);
                else if (owned.Status != ExternalPathStatus.Missing)
                    logger.LogWarning("Work folder {Work} was not cleaned up: {Details}", work, owned.Details);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Work folder {Work} could not be removed: {Error}", work, ex.Message);
            }
        }
    }

    /// <summary>
    /// T1: the result and the manifest are keyed by <c>DatName</c>, so two systems may not share one (ignoring case),
    /// whatever their source. Fails before any download, folder or audit.
    /// </summary>
    private static void CheckPlan(IEnumerable<ISystemHandler> handlers)
    {
        if (handlers.GroupBy(h => h.DatName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } clash)
            throw new InvalidOperationException(
                $"Systems {string.Join(" and ", clash.Select(h => $"'{h.Id}'"))} share the DAT name '{clash.Key}'; nothing was refreshed.");
    }

    /// <summary>
    /// T2: one request for every TitleDb system. The body is read under the cap and the job token only (the client's
    /// per-attempt timeout ends with the headers), streamed to the work folder and parsed from there once. One UTC
    /// timestamp, taken when the download ends, gives every compact file its version and download time.
    /// </summary>
    private async Task<IEnumerable<DatManifestEntry>> StageTitleDbAsync(IReadOnlyList<ISystemHandler> handlers, string work, string staged,
        JobContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Progress.SetCurrentItem(TitleDbSourceFile);
        var source = Path.Combine(work, TitleDbSourceFile);
        using (var response = await http.GetAsync(sources.TitleDbUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GET {sources.TitleDbUrl} returned {(int)response.StatusCode}.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > TitleDbByteLimit)
                throw new LimitExceededException(TitleDbByteLimit);
            await using var body = new LimitStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), TitleDbByteLimit);
            await using var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        var downloadedAt = time.GetUtcNow();
        var version = downloadedAt.UtcDateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

        IReadOnlyList<TitleDbRow> rows;
        await using (var read = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            rows = await TitleDb.ParseUpstreamAsync(read, cancellationToken).ConfigureAwait(false);

        using var compact = new MemoryStream();
        await TitleDb.WriteCompactAsync(compact, rows, cancellationToken).ConfigureAwait(false);
        var bytes = compact.ToArray();
        var entries = new List<DatManifestEntry>();
        foreach (var handler in handlers)
        {
            var relative = TitleDb.RelativePath(handler.DatName);
            var target = SafePath.Resolve(staged, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            entries.Add(new DatManifestEntry(relative, handler.DatName, version, downloadedAt, Convert.ToHexStringLower(SHA1.HashData(bytes))));
        }
        logger.LogInformation("titledb {Version}: {Count} entries for {Systems}", version, rows.Count, string.Join(", ", handlers.Select(h => h.Id)));
        context.Progress.ItemDone();
        return entries;
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}.", null, response.StatusCode);

        await using var body = new LimitStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), MaxFileBytes);
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static (string DatName, string? Version) Parse(string text, string kind, ISystemHandler handler, string relative)
    {
        try
        {
            if (kind != DatKind.NoIntro)
                return (DatFiles.ParseMetadat(text, kind).DatName, null);

            var noIntro = DatFiles.ParseNoIntro(text);
            return noIntro.DatName == handler.DatName
                ? (noIntro.DatName, noIntro.Version)
                : throw new DatParseException($"Header name is '{noIntro.DatName}', expected '{handler.DatName}'.");
        }
        catch (DatParseException ex)
        {
            throw new DatParseException($"{relative}: {ex.Message}");
        }
    }

    /// <summary>
    /// T2/G4: <c>cache/work/</c> is checked (and created when missing) before this job's folder is made; an unsafe
    /// base or an existing occupant of the job's name fails the run before anything is written, leaving it alone.
    /// </summary>
    /// <exception cref="IOException">The work folder can't be made safely.</exception>
    private string CreateWork(Guid jobId)
    {
        var baseFolder = ExternalRomPath.ClassifyDirectory(paths.Cache, Path.Combine(paths.Cache, CrashRecovery.WorkFolder));
        if (baseFolder.Status == ExternalPathStatus.Missing)
        {
            Directory.CreateDirectory(baseFolder.Path);
            baseFolder = ExternalRomPath.ClassifyDirectory(paths.Cache, baseFolder.Path);
        }
        if (baseFolder.Status != ExternalPathStatus.Found)
            throw new IOException($"The work folder can't be used: {baseFolder.Details}");

        var work = Path.Combine(baseFolder.Path, jobId.ToString());
        var occupant = ExternalRomPath.ClassifyDirectory(paths.Cache, work);
        if (occupant.Status != ExternalPathStatus.Missing)
            throw new IOException($"{work} already exists; it was left as it is.");
        Directory.CreateDirectory(work);
        return work;
    }
}
