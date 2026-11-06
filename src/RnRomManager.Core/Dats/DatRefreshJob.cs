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
/// "Refresh DATs" (spec §10.1, D43): downloads every system's 5 files into <c>cache/work/&lt;jobId&gt;/dats/</c>, parses
/// each, writes <c>dats.json</c> there, then swaps that folder in for <c>cache/dats/</c> and reloads the
/// <see cref="DatCache"/>. Any failure (HTTP, parse, wrong DAT) or a cancel before the swap keeps the old set.
/// Cancel is checked between downloads.
/// </summary>
public sealed class DatRefreshJob(
    HttpClient http, SourcesOptions sources, PathsOptions paths, DatCache cache, ISystemRegistry systems, IAuditLog audit,
    TimeProvider time, ILogger<DatRefreshJob> logger) : IJob
{
    /// <summary>The largest DAT is ~1 MiB; anything far bigger is not a DAT.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public JobType Type => JobType.DatRefresh;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var files = systems.All.SelectMany(h => DatKind.All.Select(kind => (Handler: h, Kind: kind))).ToList();
        context.Progress.SetTotal(files.Count);

        var work = Path.Combine(paths.Cache, CrashRecovery.WorkFolder, context.JobId.ToString());
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

            cancellationToken.ThrowIfCancellationRequested();
            DatManifest.Write(Path.Combine(staged, DatManifest.FileName), manifest);
            Swap(staged, work);
            // AU4: every successful run replaced cache/dats.
            audit.Record(new AuditEvent(AuditActions.DatsRefreshed, context.Actor,
                Details: string.Join(", ", manifest.Select(e => e.DatName).Distinct(StringComparer.Ordinal))));
            cache.Load();

            var versions = manifest.Where(e => e.File.StartsWith(DatKind.NoIntro + "/", StringComparison.Ordinal))
                .ToDictionary(e => e.DatName, e => e.Version);
            logger.LogInformation("DATs refreshed: {Versions}", versions);
            return new { files = manifest.Count, versions };
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
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

    /// <summary>Two directory renames on one volume (both under <c>cache/</c>); the old set is deleted last.</summary>
    private void Swap(string staged, string work)
    {
        var live = cache.Directory;
        var old = Path.Combine(work, "old");
        if (Directory.Exists(live))
            Directory.Move(live, old);
        try
        {
            Directory.Move(staged, live);
        }
        catch
        {
            if (Directory.Exists(old))
                Directory.Move(old, live); // keep the old set
            throw;
        }
    }
}
