using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Dats;

/// <summary>
/// The DATs loaded from <c>cache/dats/</c> (spec §4.1 step 9, §10.1). Each system needs its 5 files listed in
/// <c>dats.json</c> with a matching SHA1 and a clean parse; otherwise it is logged at <c>Error</c> and the system
/// has no DATs (ingestion then fails with <c>DatsMissing</c>). <see cref="Load"/> swaps the whole set at once.
/// </summary>
public sealed class DatCache(IOptions<PathsOptions> paths, ISystemRegistry systems, ILogger<DatCache> logger)
{
    public const string FolderName = "dats";

    private volatile IReadOnlyDictionary<string, DatStore> _stores = new Dictionary<string, DatStore>();

    /// <summary>T3's cap on <c>dats.json</c>; settable so tests needn't write a megabyte.</summary>
    public long MaxManifestBytes { get; set; } = DatManifest.MaxBytes;

    /// <summary>T3's cap on a compact titledb file; settable so tests needn't write 256 MiB.</summary>
    public long MaxCompactBytes { get; set; } = TitleDb.MaxCompactBytes;

    /// <summary>Test seam: runs with a compact file's path after its bytes are read, before they are re-checked.</summary>
    public Action<string>? AfterCompactRead { get; set; }

    /// <summary><c>cache/dats/</c>.</summary>
    public string Directory => Path.Combine(paths.Value.Cache, FolderName);

    /// <returns>null when the system has no (valid) DATs.</returns>
    public DatStore? For(string systemId) => _stores.GetValueOrDefault(systemId);

    /// <summary>Startup: loads the live set; a system whose DATs can't be used is logged and left without.</summary>
    public void Load() => _stores = Read(Directory, strict: false);

    /// <summary>
    /// Refresh, before the swap (T2): builds every system's store from a staged set, read exactly as startup reads
    /// the live one. Nothing is published.
    /// </summary>
    /// <param name="directory">The staged <c>dats/</c> folder, below <c>Paths:Cache</c>.</param>
    /// <exception cref="InvalidDataException">The manifest or any system's DATs can't be used.</exception>
    public IReadOnlyDictionary<string, DatStore> Prepare(string directory) => Read(directory, strict: true);

    /// <summary>Refresh, once the staged set is live: swaps in the stores <see cref="Prepare"/> built from it.</summary>
    public void Publish(IReadOnlyDictionary<string, DatStore> stores) => _stores = stores;

    private Dictionary<string, DatStore> Read(string directory, bool strict)
    {
        var stores = new Dictionary<string, DatStore>();
        var manifestPath = Path.Combine(directory, DatManifest.FileName);
        IReadOnlyList<DatManifestEntry>? manifest = null;
        try
        {
            manifest = DatManifest.ReadChecked(paths.Value.Cache, manifestPath, MaxManifestBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (strict)
                throw new InvalidDataException($"{manifestPath} can't be used: {ex.Message}", ex);
            logger.LogError("DAT cache {Path} is missing or corrupt; no system has DATs until an Admin refreshes them: {Error}", manifestPath, ex.Message);
        }

        foreach (var handler in manifest is null ? [] : systems.All)
        {
            try
            {
                var store = LoadSystem(directory, handler, manifest!);
                stores[handler.Id] = store;
                logger.LogInformation("Loaded DATs for {System}: {DatName} {Version}, {Count} ROMs", handler.Id, store.DatName, store.Version, store.Roms.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DatParseException or InvalidDataException or UnsafePathException
                or JsonException)
            {
                if (strict)
                    throw new InvalidDataException($"DATs for {handler.Id} can't be used: {ex.Message}", ex);
                logger.LogError("DATs for {System} are missing or corrupt; the system has no DATs: {Error}", handler.Id, ex.Message);
            }
        }
        return stores;
    }

    private DatStore LoadSystem(string directory, ISystemHandler handler, IReadOnlyList<DatManifestEntry> manifest)
    {
        if (handler.DatSource == DatSource.TitleDb)
            return LoadTitleDb(directory, handler, manifest);

        string ReadVerified(string kind)
        {
            var file = DatKind.RelativePath(kind, handler.DatName);
            var entry = manifest.FirstOrDefault(e => e.File == file)
                ?? throw new InvalidDataException($"{file} is not listed in {DatManifest.FileName}.");
            var bytes = File.ReadAllBytes(SafePath.Resolve(directory, file));
            if (!Convert.ToHexStringLower(SHA1.HashData(bytes)).Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{file} does not match its SHA1 in {DatManifest.FileName}.");
            return Encoding.UTF8.GetString(bytes);
        }

        var noIntro = DatFiles.ParseNoIntro(ReadVerified(handler.DatFolder));
        if (noIntro.DatName != handler.DatName)
            throw new InvalidDataException($"{DatKind.RelativePath(handler.DatFolder, handler.DatName)} is for '{noIntro.DatName}'.");
        // B2: only the listed kinds exist; a missing kind gives empty metadata.
        var metadat = handler.MetadatKinds.Select(k => DatFiles.ParseMetadat(ReadVerified(k), k)).ToList();
        return new DatStore(noIntro, metadat, handler.RomExtensions);
    }

    /// <summary>
    /// T3: exactly one manifest row for the compact path (case-only aliases count), spelled canonically, for this
    /// handler's <c>DatName</c>, with a <c>yyyy.MM.dd</c> version and a download time; then the SHA1 and every row.
    /// </summary>
    private DatStore LoadTitleDb(string directory, ISystemHandler handler, IReadOnlyList<DatManifestEntry> manifest)
    {
        var file = TitleDb.RelativePath(handler.DatName);
        var rows = manifest.Where(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count != 1)
            throw new InvalidDataException($"{file} is listed {rows.Count} times in {DatManifest.FileName}.");
        var entry = rows[0];
        if (entry.File != file)
            throw new InvalidDataException($"{DatManifest.FileName} lists {entry.File}, not {file}.");
        if (entry.DatName != handler.DatName)
            throw new InvalidDataException($"{file} is listed for '{entry.DatName}'.");
        if (!IsDateVersion(entry.Version) || entry.DownloadedAt == default)
            throw new InvalidDataException($"{file} has version '{entry.Version}' and download time {entry.DownloadedAt:O}; a yyyy.MM.dd version and a time are required.");

        // One checked, bounded read serves both the SHA1 and the parse.
        var bytes = DatManifest.ReadCheckedBytes(paths.Value.Cache, SafePath.Resolve(directory, file), MaxCompactBytes, AfterCompactRead);
        if (!Convert.ToHexStringLower(SHA1.HashData(bytes)).Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{file} does not match its SHA1 in {DatManifest.FileName}.");
        using var stream = new MemoryStream(bytes, writable: false);
        return new DatStore(TitleDb.ReadCompact(stream), entry.Version!);
    }

    private static bool IsDateVersion(string? version) =>
        DateTime.TryParseExact(version, "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
        && date.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture) == version;
}
