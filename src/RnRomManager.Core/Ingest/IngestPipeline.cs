using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

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
    IIngestReviewRepository reviews,
    InfoJsonReader reader,
    MediaService media,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    TimeProvider time,
    ILogger<IngestPipeline> logger,
    IRomStorageSettings? zipSettings = null)
{
    /// <summary>Z4: a writer at the game's system ZIP compression level.</summary>
    internal LibraryZipWriter Writer(string systemId) => new(reader, zipSettings?.Compression(systemId) ?? ZipCompression.Med);
    /// <summary>Test seams of the external ingest path.</summary>
    public ExternalIngestHooks? ExternalHooks { get; set; }

    /// <summary>Test seam: wraps the stream an embedded source is read from, in ingest and in Review.</summary>
    public Func<Stream, Stream>? WrapEmbeddedReadStream { get; set; }

    internal IngestStore IngestStore => field ??= new(this, systems, roms, games, issues, reader, media, paths, time, logger);

    internal IngestFileMoves IngestFileMoves => field ??= new(this, systems, reviews, paths, audit, auditPaths, time, logger);

    internal IngestPackager IngestPackager => field ??= new(this, mirror, reviews, paths, audit, logger);

    internal ExternalIngest ExternalIngest => field ??= new(this, paths, time, logger);

    internal IngestIdentifier IngestIdentifier => field ??= new(this, dats, roms, reviews, time, logger);

    internal SetIngest SetIngest => field ??= new(this, dats, systems, roms, games, issues, reviews, paths, time);

    /// <inheritdoc cref="SetIngest.StoreSetReleaseAsync"/>
    public Task<IngestItemResult> StoreSetReleaseAsync(
        IReadOnlyList<(IngestFile File, string Sha1)> members,
        ISystemHandler system,
        InfoJsonRom rom,
        DatMetadata metadata,
        Guid? targetGameId,
        Func<Guid, Task> afterMirror,
        AuditActor actor,
        string reviewAction,
        JobProgress progress) =>
        SetIngest.StoreSetReleaseAsync(members, system, rom, metadata, targetGameId, afterMirror, actor, reviewAction, progress);

    /// <inheritdoc cref="IngestStore.StoreReleaseAsync"/>
    public Task<IngestItemResult> StoreReleaseAsync(
        IngestFile file,
        ISystemHandler system,
        InfoJsonRom rom,
        DatMetadata metadata,
        byte[]? bytes,
        Guid? targetGameId,
        Func<Guid, Task> afterMirror,
        AuditActor actor,
        string? reviewAction,
        CancellationToken cancellationToken,
        ExternalFileSnapshot? snapshot = null,
        JobProgress? progress = null) =>
        IngestStore.StoreReleaseAsync(file, system, rom, metadata, bytes, targetGameId, afterMirror, actor, reviewAction, cancellationToken,
            snapshot, progress);

    internal (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) OpenTarget(string zipPath, string systemId, string? sha1) =>
        IngestStore.OpenTarget(zipPath, systemId, sha1);

    /// <inheritdoc cref="IngestStore.OpenTargetChecked"/>
    internal LibraryTarget OpenTargetChecked(string zipPath, Guid gameId, string systemId) =>
        IngestStore.OpenTargetChecked(zipPath, gameId, systemId);

    /// <inheritdoc cref="IngestFileMoves.FailFile"/>
    public IngestItemResult FailFile(
        IngestFile file,
        AuditActor actor,
        string reason,
        string details,
        string? sha1 = null,
        JobProgress? progress = null) =>
        IngestFileMoves.FailFile(file, actor, reason, details, sha1, progress);

    /// <inheritdoc cref="IngestFileMoves.MoveToFailed"/>
    public IngestItemResult? MoveToFailed(
        IngestFile file,
        AuditActor actor,
        string reason,
        string details,
        JobProgress? progress = null) =>
        IngestFileMoves.MoveToFailed(file, actor, reason, details, progress);

    /// <inheritdoc cref="IngestFileMoves.MoveToDuplicates"/>
    public IngestItemResult? MoveToDuplicates(IngestFile file, AuditActor actor, JobProgress? progress = null) =>
        IngestFileMoves.MoveToDuplicates(file, actor, progress);

    /// <summary>AU1: the source as recorded: <c>ingest/…</c>, or <c>ingest/&lt;archive&gt; › &lt;entry&gt;</c> for an archive entry.</summary>
    internal string AuditSource(IngestFile file) =>
        file.ArchiveEntry is { } entry ? auditPaths.ArchiveEntry(file.ArchivePath!, entry) : auditPaths.Of(file.FullPath);

    /// <summary>AU4 <c>ArchiveDeleted</c>: called by <see cref="IngestJob"/> after it deleted a fully ingested archive.</summary>
    public void RecordArchiveDeleted(IngestFile archive, AuditActor actor) =>
        audit.Record(new AuditEvent(AuditActions.ArchiveDeleted, actor, archive.SystemId, Path: auditPaths.Of(archive.FullPath)));

    /// <summary>Processes one source and completes any pending review row for that source.</summary>
    /// <param name="progress">The job's progress, for an external ROM's streamed hash and copies (job step progress); null from Review.</param>
    public async Task<IngestItemResult?> ProcessAsync(
        IngestFile file,
        Guid jobId,
        AuditActor actor,
        CircuitBreaker breaker,
        CancellationToken cancellationToken,
        JobProgress? progress = null)
    {
        var result = await ProcessCoreAsync(file, jobId, actor, breaker, cancellationToken, progress).ConfigureAwait(false);
        // I4: an external source's Error keeps its Pending row (CompleteSourceAsync would delete it); stale cleanup handles absence.
        if (result is not null && !(IngestFileMoves.IsExternal(file) && result.Outcome == IngestOutcome.Error))
            await reviews.CompleteSourceAsync(file.SourcePath, file.ArchiveEntry, result, jobId, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IngestItemResult?> ProcessCoreAsync(
        IngestFile file,
        Guid jobId,
        AuditActor actor,
        CircuitBreaker breaker,
        CancellationToken cancellationToken,
        JobProgress? progress)
    {
        // Step 1: validate.
        var folder = systems.ById(file.SystemId) ?? throw new InvalidOperationException($"No handler for ingest folder '{file.SystemId}'.");
        var family = systems.ByFamily(folder.Family);
        var extension = Path.GetExtension(file.Name);
        var byExtension = family.FirstOrDefault(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (byExtension is null)
            return IngestFileMoves.FailFile(file, actor, IngestFailures.UnsupportedType, $"'{extension}' isn't a ROM extension of the {folder.Family} family");
        if (byExtension.SourceExtension(file.Name) is null)
            return IngestFileMoves.FailFile(file, actor, IngestFailures.UnsupportedType, $"'{file.Name}' doesn't say its image type (name it like 'Game.d64.gz')");
        if (!folder.EmbedRomsInZip)
            return await ExternalIngest.ProcessExternalAsync(file, folder, family, byExtension, jobId, actor, breaker, cancellationToken, progress).ConfigureAwait(false);
        if (file.SizeBytes < byExtension.MinRomSizeBytes)
            return IngestFileMoves.FailFile(file, actor, IngestFailures.TooSmall, $"{file.SizeBytes} bytes; {byExtension.Id} ROMs are at least {byExtension.MinRomSizeBytes}");
        if (file.SizeBytes > byExtension.MaxRomSizeBytes)
            return IngestFileMoves.FailFile(file, actor, IngestFailures.TooLarge, $"{file.SizeBytes} bytes; {byExtension.Id} ROMs are at most {byExtension.MaxRomSizeBytes}");
        // Stored inside the ZIP, the ROM is read into one array; beside the ZIP it streams up to MaxRomSizeBytes.
        if (file.SizeBytes > Array.MaxLength)
            return IngestFileMoves.FailFile(file, actor, IngestFailures.TooLarge,
                $"{file.SizeBytes} bytes; ROMs stored inside the ZIP are at most {Array.MaxLength}. Store {byExtension.Id} ROMs beside the ZIP (Admin → ROM Zip Config) to import it");

        // Step 2: hash the normalised bytes (what gets stored).
        BoundedReadResult read;
        try
        {
            read = await BoundedRomRead.ReadAsync(file.FullPath, byExtension.MinRomSizeBytes, byExtension.MaxRomSizeBytes, WrapEmbeddedReadStream,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IngestItemResult.Error(IngestErrors.IoError, ex.Message, file.SystemId);
        }
        if (read.Status == BoundedReadStatus.OutOfRange)
            return read.Length < byExtension.MinRomSizeBytes
                ? IngestFileMoves.FailFile(file, actor, IngestFailures.TooSmall, $"{read.Length} bytes; {byExtension.Id} ROMs are at least {byExtension.MinRomSizeBytes}")
                : IngestFileMoves.FailFile(file, actor, IngestFailures.TooLarge, $"{read.Length} bytes; {byExtension.Id} ROMs are at most {byExtension.MaxRomSizeBytes}");
        // Changed while read: an Error leaves the file where it is.
        if (read.Status == BoundedReadStatus.Changed)
            return IngestItemResult.Error(IngestErrors.IoError, read.Details, file.SystemId);
        var bytes = BoundedRomRead.Normalize(byExtension, read.Bytes!);
        var hashes = RomHashes.Of(bytes);
        logger.LogDebug("{Path}: {Size} bytes, crc32 {Crc32}, sha1 {Sha1}", file.FullPath, bytes.Length, hashes.Crc32, hashes.Sha1);
        return await IngestIdentifier.IdentifyAsync(file, folder, family, hashes, bytes.Length, bytes, bytes, null, jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// B7/B7a: one planned cue, TOC, CloneCD or MDS set (the cue or <c>.toc</c> first, then the files it names; the <c>.ccd</c>, <c>.img</c>, <c>.sub</c>; or the <c>.mds</c>, <c>.mdf</c>), processed as one unit to the end:
    /// every member is hashed once, the member whose SHA1 the DAT lists is the data track, and the set is stored as one
    /// release (the other members in its <c>files</c>), queued for Review member by member, or left alone. Each member's
    /// own Pending review row is completed by its result, as <see cref="ProcessAsync"/> does for one file.
    /// </summary>
    /// <returns>One result per member, in <paramref name="members"/> order.</returns>
    public async Task<IReadOnlyList<IngestItemResult>> ProcessSetAsync(
        IReadOnlyList<IngestFile> members,
        Guid jobId,
        AuditActor actor,
        CircuitBreaker breaker,
        JobProgress? progress = null)
    {
        var results = await SetIngest.ProcessSetCoreAsync(members, jobId, actor, breaker, progress).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < members.Count; i++)
        {
            // I4: an Error keeps the member's Pending row.
            if (results[i].Outcome != IngestOutcome.Error)
                await reviews.CompleteSourceAsync(members[i].SourcePath, null, results[i], jobId, now, CancellationToken.None).ConfigureAwait(false);
        }
        return results;
    }

    internal static string? Join(params string?[] parts) =>
        parts.OfType<string>().ToList() is { Count: > 0 } notes ? string.Join("; ", notes) : null;

}
