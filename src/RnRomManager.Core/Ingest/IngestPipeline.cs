using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Integrity;
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

/// <summary>§7.2 reason codes for <c>Failed</c> items (spec §23.1): the file moves to <c>_failed/</c>.</summary>
public static class IngestFailures
{
    public const string UnsupportedType = "UnsupportedType";
    public const string TooSmall = "TooSmall";
    public const string TooLarge = "TooLarge";
    public const string InvalidHeader = "InvalidHeader";
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
    IIntegrityIssueRepository issues,
    InfoJsonReader reader,
    MediaService media,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z"];

    /// <returns>null when the file isn't handled yet and stays in ingest untouched.</returns>
    public async Task<IngestItemResult?> ProcessAsync(IngestFile file, Guid jobId, CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        // Step 1: validate.
        var folder = systems.ById(file.SystemId) ?? throw new InvalidOperationException($"No handler for ingest folder '{file.SystemId}'.");
        var family = systems.ByFamily(folder.Family);
        var extension = Path.GetExtension(file.Name);
        if (ArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            // shortcut: archives are left in ingest until S28.
            logger.LogDebug("{Path}: archives are handled from S28; left in ingest", file.FullPath);
            return null;
        }
        var byExtension = family.FirstOrDefault(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (byExtension is null)
            return Fail(file, jobId, IngestFailures.UnsupportedType, $"'{extension}' isn't a ROM extension of the {folder.Family} family");
        if (file.SizeBytes < byExtension.MinRomSizeBytes)
            return Fail(file, jobId, IngestFailures.TooSmall, $"{file.SizeBytes} bytes; {byExtension.Id} ROMs are at least {byExtension.MinRomSizeBytes}");
        if (file.SizeBytes > byExtension.MaxRomSizeBytes)
            return Fail(file, jobId, IngestFailures.TooLarge, $"{file.SizeBytes} bytes; {byExtension.Id} ROMs are at most {byExtension.MaxRomSizeBytes}");

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

        // Step 3: global dedupe, or self-repair of a damaged library entry. shortcut: review-item rules come in S29.
        if (await roms.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false) is { } known)
            return await DuplicateOrRepairAsync(file, known, bytes, hashes.Sha1, cancellationToken).ConfigureAwait(false);

        // Step 4: header from every handler of the family; the DAT match decides the system (D23).
        var headers = family.ToDictionary(h => h.Id, h => h.ReadHeader(bytes));
        var match = family
            .Select(h => (Handler: h, Store: dats.For(h.Id)))
            .Select(x => (x.Handler, x.Store, Rom: x.Store?.BySha1(hashes.Sha1)))
            .FirstOrDefault(x => x.Rom is not null);
        if (match.Rom is null)
        {
            // Step 5. Every ReadHeader null (headerless system) counts as plausible.
            var found = headers.Values.FirstOrDefault(h => h is not null);
            if (found is { IsPlausible: false })
                return Fail(file, jobId, IngestFailures.InvalidHeader, "no DAT match, and the header's logo and checksum are both invalid", hashes.Sha1);
            RenameToClassifiedExtension(file, found, family);
            // shortcut: Review NoDatMatch (upsert + suggestions) comes in S29; until then the file waits in ingest.
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

        var (existing, opened, error) = OpenTarget(row.ZipPath, system.Id, result.Sha1);
        if (error is not null)
            return error;
        var current = opened!;

        using (existing)
        {
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

    /// <summary>§6.1: every write starts from the ZIP's own <c>info.json</c>.</summary>
    private (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) OpenTarget(string zipPath, string systemId, string? sha1)
    {
        LibraryZip zip;
        try
        {
            zip = LibraryZip.Open(SafePath.Resolve(paths.Value.Roms, zipPath), reader);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, null, IngestItemResult.Error(IngestErrors.TargetZipUnreadable, $"{zipPath}: {ex.Message}", systemId, sha1));
        }

        IngestItemResult? error = zip.InfoJson switch
        {
            { Kind: InfoJsonReadResult.Outcome.Ok } => null,
            { Kind: InfoJsonReadResult.Outcome.NewerSchema } newer =>
                IngestItemResult.Error(IngestErrors.InfoJsonNewerSchema, $"{zipPath}: schemaVersion {newer.FileVersion}", systemId, sha1),
            _ => IngestItemResult.Error(IngestErrors.TargetZipUnreadable, $"{zipPath}: info.json {zip.InfoJson?.Rule ?? "missing"}", systemId, sha1),
        };
        if (error is null)
            return (zip, zip.InfoJson!.Game, null);
        zip.Dispose();
        return (null, null, error);
    }

    /// <summary>
    /// Step 3: the SHA1 is already in the library. Its entry has an open <c>HashMismatch</c>/<c>EntryMissing</c>/
    /// <c>EntrySizeMismatch</c> issue → rewrite it from this file and resolve the issue (<c>Repaired</c>, D30);
    /// otherwise <c>Duplicate</c> → <c>_duplicates/</c>.
    /// </summary>
    private async Task<IngestItemResult> DuplicateOrRepairAsync(IngestFile file, RomRow known, byte[] bytes, string sha1, CancellationToken cancellationToken)
    {
        var game = await games.GetAsync(known.GameId, cancellationToken).ConfigureAwait(false);
        var entryName = LibraryZip.RomEntry(known.FileName);
        var open = game is null
            ? []
            : await issues.ListOpenAsync(game.ZipPath, entryName, IntegrityIssueType.SelfRepairable, cancellationToken).ConfigureAwait(false);

        if (open.Count == 0)
        {
            logger.LogWarning("{Path}: duplicate of {Zip} › {Entry} (sha1 {Sha1}, size {Size})",
                file.FullPath, game?.ZipPath, entryName, sha1, bytes.Length);
            var moved = MoveAside(file, StorageFolders.DuplicatesFolder);
            return moved.Error ?? new IngestItemResult(IngestOutcome.Duplicate, Details: $"same as {game?.ZipPath} › {entryName}",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
        }

        var (existing, current, error) = OpenTarget(game!.ZipPath, known.SystemId, sha1);
        if (error is not null)
            return error;
        using (existing)
        {
            var now = time.GetUtcNow();
            var repaired = current! with
            {
                Roms = current.Roms.Select(r => r.Metadata.Sha1 == sha1 ? r with { Metadata = r.Metadata with { DateModified = now } } : r).ToList(),
                Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
            };
            var entries = new Dictionary<string, Func<Stream>> { [entryName] = () => new MemoryStream(bytes) };
            var result = new IngestItemResult(IngestOutcome.Repaired, Details: $"rewrote {game.ZipPath} › {entryName} ({string.Join(", ", open.Select(i => i.Type))})",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
            return await PackageAsync(file, existing, game.ZipPath, repaired, entries, result, cancellationToken,
                () => issues.ResolveAsync(open.Select(i => i.Id).ToList(), now.UtcDateTime, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>A file problem: move it to <c>_failed/</c> with <c>&lt;name&gt;.error.txt</c> (spec §5).</summary>
    private IngestItemResult Fail(IngestFile file, Guid jobId, string reason, string details, string? sha1 = null)
    {
        logger.LogWarning("{Path}: Failed {Reason}: {Details}", file.FullPath, reason, details);
        var moved = MoveAside(file, StorageFolders.FailedFolder);
        if (moved.Error is not null)
            return moved.Error;
        try
        {
            File.WriteAllText(moved.Path + ".error.txt",
                $"reason: {reason}\ndetails: {details}\njob: {jobId}\ntime: {time.GetUtcNow().UtcDateTime:O}\nsource: {file.FullPath}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't write the .error.txt: {Error}", moved.Path, ex.Message);
        }
        return new IngestItemResult(IngestOutcome.Failed, reason, details, file.SystemId, Sha1: sha1);
    }

    /// <summary>Moves the source into <c>ingest/&lt;system&gt;/&lt;folder&gt;/</c>, with a ` (N)` suffix on a clash (any case).</summary>
    private (string Path, IngestItemResult? Error) MoveAside(IngestFile file, string folder)
    {
        var directory = Path.Combine(Path.GetDirectoryName(file.FullPath)!, folder);
        try
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, FileNameRules.FirstFreeIn(directory, file.Name));
            File.Move(file.FullPath, target);
            return (target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("", IngestItemResult.Error(IngestErrors.IoError, $"couldn't move to {folder}/: {ex.Message}", file.SystemId));
        }
    }

    /// <summary>
    /// §8.4: an unmatched file whose extension doesn't fit its header classification is renamed in place to the
    /// classified handler's first extension (<c>Warn</c>).
    /// </summary>
    private void RenameToClassifiedExtension(IngestFile file, RomHeader? header, IReadOnlyList<ISystemHandler> family)
    {
        var classified = family.FirstOrDefault(h => h.Id == header?.ClassifiedSystemId);
        if (classified is null || classified.RomExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase))
            return;
        var directory = Path.GetDirectoryName(file.FullPath)!;
        var renamed = FileNameRules.FirstFreeIn(directory, Path.GetFileNameWithoutExtension(file.Name) + classified.RomExtensions[0]);
        try
        {
            File.Move(file.FullPath, Path.Combine(directory, renamed));
            logger.LogWarning("{Path}: header says {System}; renamed to {Name}", file.FullPath, classified.Id, renamed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't rename to {Name}: {Error}", file.FullPath, renamed, ex.Message);
        }
    }

    /// <summary>Steps 9–11.</summary>
    /// <param name="afterMirror">Runs once the DB is updated, before the source is deleted.</param>
    private async Task<IngestItemResult> PackageAsync(
        IngestFile file, LibraryZip? existing, string zipPath, InfoJsonGame game, Dictionary<string, Func<Stream>> entries,
        IngestItemResult result, CancellationToken cancellationToken, Func<Task>? afterMirror = null)
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
        if (afterMirror is not null)
            await afterMirror().ConfigureAwait(false);

        try
        {
            File.Delete(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: ingested into {Zip} but couldn't be deleted ({Error}); the next run sees a duplicate", file.FullPath, zipPath, ex.Message);
        }
        logger.LogInformation("{Path}: {Outcome} into {Zip}", file.FullPath, result.Outcome, zipPath);
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
