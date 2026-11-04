using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>What happened to one ingest item: becomes its <c>job_items</c> row.</summary>
public sealed record IngestItemResult(
    string Outcome, string? Reason = null, string? Details = null, string? SystemId = null, Guid? GameId = null, string? Sha1 = null)
{
    public static IngestItemResult Error(string reason, string? details, string? systemId = null, string? sha1 = null) =>
        new(IngestOutcome.Error, reason, details, systemId, Sha1: sha1);
}

/// <summary>§7.2 reason codes for <c>Error</c> items (spec §23.1).</summary>
public static class IngestErrors
{
    public const string IoError = "IoError";
    public const string VerifyFailed = "VerifyFailed";
    public const string DbWriteFailed = "DbWriteFailed";
    public const string TargetZipUnreadable = "TargetZipUnreadable";
    public const string InfoJsonNewerSchema = "InfoJsonNewerSchema";
    public const string Unexpected = "Unexpected";
}

/// <summary>
/// One loose file through spec §7.2: validate → hash → identify by DAT → group → metadata → media → package (atomic
/// ZIP) → mirror (DB) → delete the source. Every write starts from the game's ZIP, never from DB rows.
/// </summary>
public sealed class IngestPipeline(
    DatCache dats,
    ISystemRegistry systems,
    IRomRepository roms,
    IGameRepository games,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    MediaService media,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z"];

    /// <returns>null when the file isn't handled yet and stays in ingest untouched.</returns>
    public async Task<IngestItemResult?> ProcessAsync(IngestFile file, CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        // Step 1: validate. shortcut: archives (S28), unsupported types and size limits (S27) are left in ingest for now.
        var folder = systems.ById(file.SystemId) ?? throw new InvalidOperationException($"No handler for ingest folder '{file.SystemId}'.");
        var family = systems.ByFamily(folder.Family);
        var extension = Path.GetExtension(file.Name);
        var byExtension = family.FirstOrDefault(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (ArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || byExtension is null
            || file.SizeBytes < byExtension.MinRomSizeBytes || file.SizeBytes > byExtension.MaxRomSizeBytes)
        {
            logger.LogDebug("{Path}: not handled until the failure/archive steps; left in ingest", file.FullPath);
            return null;
        }

        // Step 2: hash the normalised bytes (what gets stored).
        byte[] bytes;
        try
        {
            bytes = byExtension.NormalizeForHashing(await File.ReadAllBytesAsync(file.FullPath, cancellationToken).ConfigureAwait(false)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IngestItemResult.Error(IngestErrors.IoError, ex.Message, file.SystemId);
        }
        var hashes = new RomHashes(bytes);
        logger.LogDebug("{Path}: {Size} bytes, crc32 {Crc32}, sha1 {Sha1}", file.FullPath, bytes.Length, hashes.Crc32, hashes.Sha1);

        // Step 3. shortcut: duplicates and self-repair come in S27; until then a known SHA1 is left in ingest.
        if (await roms.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false) is not null)
        {
            logger.LogDebug("{Path}: SHA1 already in the library; left for the dedupe step", file.FullPath);
            return null;
        }

        // Step 4: header from every handler of the family; the DAT match decides the system (D23).
        var headers = family.ToDictionary(h => h.Id, h => h.ReadHeader(bytes));
        var match = family
            .Select(h => (Handler: h, Store: dats.For(h.Id)))
            .Select(x => (x.Handler, x.Store, Rom: x.Store?.BySha1(hashes.Sha1)))
            .FirstOrDefault(x => x.Rom is not null);
        if (match.Rom is null)
        {
            // shortcut: Step 5 (Failed InvalidHeader / Review NoDatMatch) comes in S27.
            logger.LogDebug("{Path}: no DAT match; left for the review step", file.FullPath);
            return null;
        }
        var (system, store, datRom) = (match.Handler, match.Store!, match.Rom);
        if (system.Id != file.SystemId)
            logger.LogInformation("{Path}: dropped in ingest/{Folder} but it's a {System} ROM; stored under roms/{System}", file.FullPath, file.SystemId, system.Id, system.Id);

        // Step 6: group by system + base title.
        var baseTitle = NoIntroName.Parse(datRom.GameName).BaseTitle;
        var owners = await roms.ListGameIdsByBaseTitleAsync(system.Id, baseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
        {
            // shortcut: Review AmbiguousGroup (D32) comes with the review steps; left in ingest.
            logger.LogDebug("{Path}: {Count} games share base title '{BaseTitle}'; left for review", file.FullPath, owners.Count, baseTitle);
            return null;
        }

        var now = time.GetUtcNow();
        var tags = NoIntroName.Parse(Path.GetFileNameWithoutExtension(datRom.RomName));
        var rom = new InfoJsonRom
        {
            FileName = datRom.RomName,
            OriginalFileName = file.Name,
            BaseTitle = baseTitle,
            Regions = tags.Regions,
            Languages = tags.Languages,
            Revision = tags.Revision,
            Flags = tags.Flags,
            IsPrimary = false,
            Identification = new InfoJsonIdentification(IdentificationMethod.Dat, store.DatName, store.Version, datRom.GameName),
            Header = headers[system.Id] is { } header ? JsonSerializer.SerializeToNode(header.Fields) as JsonObject : null,
            Metadata = new InfoJsonRomMetadata(bytes.Length, hashes.Crc32, hashes.Md5, hashes.Sha1, now, now, file.FullPath),
        };
        var metadata = store.MetadataFor(datRom.Crc32);
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: hashes.Sha1);

        return owners.Count == 0
            ? await NewGameAsync(file, system, rom, metadata, bytes, breaker, result, now, cancellationToken).ConfigureAwait(false)
            : await AddToGameAsync(file, system, owners[0], rom, metadata, bytes, breaker, result, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestItemResult> NewGameAsync(
        IngestFile file, ISystemHandler system, InfoJsonRom rom, DatMetadata metadata, byte[] bytes, CircuitBreaker breaker,
        IngestItemResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Step 7: a new game takes every metadata field. Step 8: media.
        var found = await media.FindAsync(system, [rom.Identification.DatGameName], breaker, cancellationToken).ConfigureAwait(false);
        var title = rom.BaseTitle;
        var game = new InfoJsonGame
        {
            SchemaVersion = InfoJsonReader.CurrentVersion,
            GameId = Guid.NewGuid(),
            System = system.Id,
            Title = title,
            DisplayTitle = DisplayTitle.From(title),
            ReleaseYear = metadata.ReleaseYear,
            Developers = metadata.Developers,
            Publishers = metadata.Publishers,
            Genres = metadata.Genres,
            Description = null,
            Roms = WithPrimary([rom]),
            Images = [found.BoxArt.ToInfoJson(now), .. found.Snap is { } snap ? [snap.ToInfoJson(now)] : Array.Empty<InfoJsonImage>()],
            Metadata = new InfoJsonGameMetadata(now, now, AppInfo.Version),
        };

        // §6.1: Sanitize(title).zip in roms/<system>/<bucket>/; a clash (any case) with another game gets " (2)"….
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        var folder = Path.Combine(paths.Value.Roms, system.Id, Bucket.For(zipName));
        Directory.CreateDirectory(folder);
        zipName = FileNameRules.FirstFreeIn(folder, zipName);
        var zipPath = $"{system.Id}/{Bucket.For(zipName)}/{zipName}";

        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes) };
        AddImages(entries, found.BoxArt, found.Snap);
        return await PackageAsync(file, null, zipPath, game, entries, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestItemResult> AddToGameAsync(
        IngestFile file, ISystemHandler system, Guid gameId, InfoJsonRom rom, DatMetadata metadata, byte[] bytes, CircuitBreaker breaker,
        IngestItemResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await games.GetAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return IngestItemResult.Error(IngestErrors.TargetZipUnreadable, $"game {gameId} has ROM rows but no games row", system.Id, result.Sha1);

        // §6.1: every write starts from the ZIP's own info.json.
        LibraryZip existing;
        try
        {
            existing = LibraryZip.Open(SafePath.Resolve(paths.Value.Roms, row.ZipPath), reader);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return IngestItemResult.Error(IngestErrors.TargetZipUnreadable, $"{row.ZipPath}: {ex.Message}", system.Id, result.Sha1);
        }

        using (existing)
        {
            switch (existing.InfoJson)
            {
                case { Kind: InfoJsonReadResult.Outcome.NewerSchema } newer:
                    return IngestItemResult.Error(IngestErrors.InfoJsonNewerSchema, $"{row.ZipPath}: schemaVersion {newer.FileVersion}", system.Id, result.Sha1);
                case not { Kind: InfoJsonReadResult.Outcome.Ok }:
                    return IngestItemResult.Error(IngestErrors.TargetZipUnreadable,
                        $"{row.ZipPath}: info.json {existing.InfoJson?.Rule ?? "missing"}", system.Id, result.Sha1);
            }
            var current = existing.InfoJson.Game!;

            // §6.1 entry name clash: " (2)"… before the extension.
            rom = rom with { FileName = FileNameRules.FirstFree(rom.FileName, current.Roms.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains) };
            var allRoms = WithPrimary([.. current.Roms, rom]);

            // Step 7: only fields that are still empty.
            var game = current with
            {
                ReleaseYear = current.ReleaseYear ?? metadata.ReleaseYear,
                Developers = current.Developers.Count > 0 ? current.Developers : metadata.Developers,
                Publishers = current.Publishers.Count > 0 ? current.Publishers : metadata.Publishers,
                Genres = current.Genres.Count > 0 ? current.Genres : metadata.Genres,
                Roms = allRoms,
                Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
            };

            var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes) };

            // Step 8: only when the game still has the default box art.
            var boxArt = current.Images.FirstOrDefault(i => i.Type == ImageType.BoxArt);
            if (boxArt is null || boxArt.IsDefault)
            {
                var names = allRoms.OrderBy(Rank, PrimaryReleaseRanker.Instance).Select(r => r.Identification.DatGameName);
                var found = await media.FindAsync(system, names, breaker, cancellationToken).ConfigureAwait(false);
                var newBoxArt = found.BoxArt.IsDefault && boxArt is not null ? null : found.BoxArt;
                var newSnap = current.Images.Any(i => i.Type == ImageType.Snap) ? null : found.Snap;
                game = game with
                {
                    Images = [.. current.Images.Where(i => !(newBoxArt is not null && i.Type == ImageType.BoxArt)),
                        .. newBoxArt is null ? Array.Empty<InfoJsonImage>() : [newBoxArt.ToInfoJson(now)],
                        .. newSnap is null ? Array.Empty<InfoJsonImage>() : [newSnap.ToInfoJson(now)]],
                };
                AddImages(entries, newBoxArt, newSnap);
            }

            return await PackageAsync(file, existing, row.ZipPath, game, entries, result with { GameId = gameId }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Steps 9–11.</summary>
    private async Task<IngestItemResult> PackageAsync(
        IngestFile file, LibraryZip? existing, string zipPath, InfoJsonGame game, Dictionary<string, Func<Stream>> entries,
        IngestItemResult result, CancellationToken cancellationToken)
    {
        result = result with { GameId = game.GameId };
        var target = SafePath.Resolve(paths.Value.Roms, zipPath);
        try
        {
            await new LibraryZipWriter(reader).WriteAsync(target, existing, new LibraryZipChanges(game, entries), cancellationToken).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            logger.LogError(ex, "{Path}: verify failed writing {Zip}", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.VerifyFailed, Details = ex.Message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "{Path}: writing {Zip} failed", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.IoError, Details = ex.Message };
        }

        var info = new FileInfo(target);
        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Path}: {Zip} was written but the DB update failed", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed, Details = ex.Message };
        }

        try
        {
            File.Delete(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: ingested into {Zip} but couldn't be deleted ({Error}); the next run sees a duplicate", file.FullPath, zipPath, ex.Message);
        }
        logger.LogInformation("{Path}: ingested as {Rom} into {Zip}", file.FullPath, game.Roms.Last().FileName, zipPath);
        return result;
    }

    private static void AddImages(Dictionary<string, Func<Stream>> entries, MediaImage? boxArt, MediaImage? snap)
    {
        foreach (var image in new[] { boxArt, snap }.OfType<MediaImage>())
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(image.Bytes);
    }

    private static RankedRelease Rank(InfoJsonRom r) => new(r.FileName, r.Regions, r.Languages, r.Revision, r.Flags);

    /// <summary>§6.1: exactly one primary, recomputed on every write.</summary>
    private static IReadOnlyList<InfoJsonRom> WithPrimary(IReadOnlyList<InfoJsonRom> releases)
    {
        var primary = PrimaryReleaseRanker.Primary(releases, Rank);
        return releases.Select(r => r with { IsPrimary = ReferenceEquals(r, primary) }).ToList();
    }

    private sealed class RomHashes(byte[] bytes)
    {
        public string Crc32 { get; } = Storage.Crc32.ToHex(bytes);
        public string Md5 { get; } = Convert.ToHexStringLower(MD5.HashData(bytes));
        public string Sha1 { get; } = Convert.ToHexStringLower(SHA1.HashData(bytes));
    }
}
