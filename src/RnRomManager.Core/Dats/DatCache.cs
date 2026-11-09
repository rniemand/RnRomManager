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

    /// <summary><c>cache/dats/</c>.</summary>
    public string Directory => Path.Combine(paths.Value.Cache, FolderName);

    /// <returns>null when the system has no (valid) DATs.</returns>
    public DatStore? For(string systemId) => _stores.GetValueOrDefault(systemId);

    public void Load()
    {
        var stores = new Dictionary<string, DatStore>();
        var manifestPath = Path.Combine(Directory, DatManifest.FileName);
        IReadOnlyList<DatManifestEntry>? manifest = null;
        try
        {
            manifest = DatManifest.Read(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError("DAT cache {Path} is missing or corrupt; no system has DATs until an Admin refreshes them: {Error}", manifestPath, ex.Message);
        }

        foreach (var handler in manifest is null ? [] : systems.All)
        {
            try
            {
                var store = LoadSystem(handler, manifest!);
                stores[handler.Id] = store;
                logger.LogInformation("Loaded DATs for {System}: {DatName} {Version}, {Count} ROMs", handler.Id, store.DatName, store.Version, store.Roms.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DatParseException or InvalidDataException or UnsafePathException)
            {
                logger.LogError("DATs for {System} are missing or corrupt; the system has no DATs: {Error}", handler.Id, ex.Message);
            }
        }

        _stores = stores;
    }

    private DatStore LoadSystem(ISystemHandler handler, IReadOnlyList<DatManifestEntry> manifest)
    {
        string ReadVerified(string kind)
        {
            var file = DatKind.RelativePath(kind, handler.DatName);
            var entry = manifest.FirstOrDefault(e => e.File == file)
                ?? throw new InvalidDataException($"{file} is not listed in {DatManifest.FileName}.");
            var bytes = File.ReadAllBytes(SafePath.Resolve(Directory, file));
            if (!Convert.ToHexStringLower(SHA1.HashData(bytes)).Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{file} does not match its SHA1 in {DatManifest.FileName}.");
            return Encoding.UTF8.GetString(bytes);
        }

        var noIntro = DatFiles.ParseNoIntro(ReadVerified(DatKind.NoIntro));
        if (noIntro.DatName != handler.DatName)
            throw new InvalidDataException($"{DatKind.RelativePath(DatKind.NoIntro, handler.DatName)} is for '{noIntro.DatName}'.");
        var metadat = DatKind.All.Where(k => k != DatKind.NoIntro).Select(k => DatFiles.ParseMetadat(ReadVerified(k), k)).ToList();
        return new DatStore(noIntro, metadat, handler.RomExtensions);
    }
}
