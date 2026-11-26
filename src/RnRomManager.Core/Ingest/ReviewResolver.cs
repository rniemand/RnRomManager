using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// Review actions (§7.5); shares the ingest writer and the global job lock. Resolve runs inside a
/// <see cref="ReviewResolveJob"/>, whose runner holds the lock (B2); Dismiss and Delete take it themselves.
/// </summary>
public sealed class ReviewResolver(
    JobLock jobLock,
    IIngestReviewRepository reviews,
    IGameRepository games,
    IRomRepository roms,
    DbSync dbSync,
    IngestPipeline pipeline,
    DatCache dats,
    ISystemRegistry systems,
    ArchiveExtractor extractor,
    ReviewSets sets,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    TimeProvider time,
    ILogger<ReviewResolver> logger)
{
    private ReviewSourceDeleter SourceDeleter => field ??= new(jobLock, reviews, systems, paths, audit, auditPaths, logger);

    private ReviewExternalPreparation ExternalPreparation =>
        field ??= new(reviews, games, roms, dbSync, pipeline, systems, extractor, paths, time, logger);

    /// <summary>
    /// B2: resolves inside a job. The caller (the job runner) holds the lock; nothing here is cancellable. The result's
    /// <see cref="ReviewResolutionResult.Items"/> are the rows acted on: the item, or its set's members in set order
    /// (B14); empty when it no longer exists or isn't Pending.
    /// </summary>
    /// <param name="actor">The job's actor (AU3); <see cref="AuditActor.UserId"/> is required.</param>
    public async Task<ReviewResolutionResult> ResolveInJobAsync(
        long id,
        ReviewResolutionRequest request,
        AuditActor actor,
        JobProgress? progress)
    {
        var item = await reviews.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        // B14 step 1: a set member's set is found again with the lock held.
        var set = IsSetMemberCandidate(item, systems) ? await sets.FindAsync(item, CancellationToken.None).ConfigureAwait(false) : null;
        var items = set?.Select(m => m.Row).ToList() ?? [item];
        progress?.SetTotal(items.Count);
        if (set is null)
            progress?.SetCurrentItem(item.OriginalFileName); // a set's members are reported one by one as they are hashed
        var result = await ActCoreAsync(id, item, request, actor, CancellationToken.None, progress, set).ConfigureAwait(false);
        if (set is null)
            progress?.ItemDone();
        return result with { Items = items };
    }

    public async Task<ReviewResolutionResult> DismissAsync(long id, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Resolving review items");
        if (held is null)
            return new("JobRunning");
        var item = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        var set = IsSetMemberCandidate(item, systems) ? await sets.FindAsync(item, ct).ConfigureAwait(false) : null;
        return await ActCoreAsync(id, item, null, actor, ct, null, set).ConfigureAwait(false);
    }

    /// <summary>B12/B15, CloneCD B7: a set member candidate on a <c>FileSets</c> system that isn't dismissible and whose set isn't found can't be resolved; no set member can be dismissed.</summary>
    public const string SetFileNotResolvable = "SetFileNotResolvable";

    /// <summary>
    /// B12, CloneCD B7: true for every dismissible item, and for a set member candidate whose set
    /// <see cref="ReviewSets.FindAsync"/> finds.
    /// </summary>
    public async Task<bool> IsResolvableAsync(ReviewItem item, CancellationToken cancellationToken = default) =>
        (IsSetMemberCandidate(item, systems) && await sets.FindAsync(item, cancellationToken).ConfigureAwait(false) is not null)
        || IsDismissible(item, systems, inSet: false);

    /// <summary>CloneCD B7, D10: a <c>.cue</c>, <c>.bin</c>, <c>.ccd</c>, <c>.img</c>, <c>.sub</c>, <c>.toc</c>, <c>.mds</c> or <c>.mdf</c> item (by its <c>originalFileName</c>, ignoring case) on a <c>FileSets</c> system.</summary>
    public static bool IsSetMemberCandidate(ReviewItem item, ISystemRegistry systems) =>
        systems.ById(item.SystemId) is { FileSets: true }
        && Path.GetExtension(item.OriginalFileName).ToLowerInvariant() is ".cue" or ".bin" or ".ccd" or ".img" or ".sub" or ".toc" or ".mds" or ".mdf";

    /// <summary>
    /// B15, CloneCD B7: false for every set member candidate but a lone PlayStation <c>.img</c>, so a set is never half
    /// dismissed.
    /// </summary>
    /// <param name="inSet">Whether <see cref="ReviewSets.FindAsync"/> found the item's set.</param>
    public static bool IsDismissible(ReviewItem item, ISystemRegistry systems, bool inSet) =>
        !IsSetMemberCandidate(item, systems)
        || (item.SystemId == "psx" && !inSet && Path.GetExtension(item.OriginalFileName).Equals(".img", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Hard delete (D2–D4): removes the source file from the ingest folder (the whole archive for an archive entry) and
    /// every pending review row of that source. A source that's already gone still removes the rows.
    /// </summary>
    public Task<ReviewResolutionResult> DeleteSourceAsync(long id, AuditActor actor, CancellationToken ct) =>
        SourceDeleter.DeleteSourceAsync(id, actor, ct);

    /// <summary>
    /// Q4: <see cref="DeleteSourceAsync"/> for each id in order under one lock acquisition. An id that no longer exists or
    /// isn't Pending (e.g. removed with its archive earlier in the batch) is skipped. Null when a job holds the lock (nothing deleted).
    /// </summary>
    public Task<ReviewBatchDeleteResult?> DeleteSourcesAsync(IReadOnlyList<long> ids, AuditActor actor, CancellationToken ct) =>
        SourceDeleter.DeleteSourcesAsync(ids, actor, ct);

    internal static bool IsExternal(ReviewItem item, ISystemRegistry systems) => systems.ById(item.SystemId) is { EmbedRomsInZip: false };

    /// <summary>Resolve (<paramref name="request"/> set) or Dismiss of a Pending <paramref name="item"/>; the caller holds the lock.</summary>
    /// <param name="set">B14: the item's set, found under the lock; the whole set is resolved as one release.</param>
    private async Task<ReviewResolutionResult> ActCoreAsync(
        long id,
        ReviewItem item,
        ReviewResolutionRequest? request,
        AuditActor actor,
        CancellationToken ct,
        JobProgress? progress,
        IReadOnlyList<(IngestFile File, ReviewItem Row)>? set = null)
    {
        var userId = actor.UserId!.Value; // review actions always have a user: the signed-in one, or the job's requester
        if (!IsDismissible(item, systems, set is not null) && (request is null || set is null))
            return new(SetFileNotResolvable);
        var external = IsExternal(item, systems);
        string source;
        ExternalFileSnapshot? sourceSnapshot = null;
        if (external)
        {
            // I4: the source is classified before it is opened; only a confirmed absence is SourceMissing.
            var check = ExternalRomPath.ClassifyFile(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath);
            if (check.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
            {
                logger.LogWarning("Review item {ReviewId}: its source {Path} can't be used safely: {Details}", id, check.Path, check.Details);
                return new("IoError");
            }
            source = check.Path;
            sourceSnapshot = check.Snapshot;
            // T9: once started, an external resolution runs to the end even if the request goes away (a multi-GB
            // hash and move can outlast a reverse proxy's read timeout).
            if (check.Status == ExternalPathStatus.Found)
                ct = CancellationToken.None;
        }
        else
        {
            source = SafePath.Resolve(paths.Value.Ingest, Path.GetRelativePath(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath));
        }
        if (!File.Exists(source))
        {
            await reviews.DeleteAsync([id], ct).ConfigureAwait(false);
            return new("SourceMissing");
        }
        var work = Path.Combine(paths.Value.Cache, CrashRecovery.WorkFolder, Guid.NewGuid().ToString());
        try
        {
            var file = new IngestFile(item.SystemId, source, item.ArchivePath is null ? Path.GetFileName(source) : item.OriginalFileName,
                item.SizeBytes, File.GetLastWriteTimeUtc(source));
            if (request is null)
            {
                if (item.ArchivePath is null && pipeline.MoveToFailed(file, actor, "Dismissed", "dismissed by an Admin") is { } failed)
                    return new(failed.Reason);
                // I4: an external source has moved, so its row is updated even if the request ends now.
                await reviews.SetResolutionAsync(id, "Dismissed", null, userId, time.GetUtcNow().UtcDateTime, external ? CancellationToken.None : ct).ConfigureAwait(false);
                return new();
            }

            var system = systems.ById(item.SystemId)!;
            // T9: an external release keeps its original extension, as does a KeepsSourceExtension system's; an embedded one takes the handler's first (its bytes may be normalised).
            var originalExtension = system.SourceExtension(item.OriginalFileName) ?? Path.GetExtension(item.OriginalFileName).ToLowerInvariant();
            ExternalFileSnapshot? preparedSnapshot = null;
            if (external)
            {
                (var prepared, preparedSnapshot, var refused) = ExternalPreparation.PrepareExternal(id, item, file, source, sourceSnapshot!, work, system);
                if (refused is not null)
                    return new(refused);
                file = prepared!;
                if (item.ArchivePath is not null && await ExternalPreparation.RecoverCommittedAsync(id, item, file, preparedSnapshot!, userId).ConfigureAwait(false) is { } recovered)
                    return recovered;
            }
            Guid? target = null;
            var metadata = DatMetadata.Empty;
            var identification = new InfoJsonIdentification(IdentificationMethod.Manual, null, null, null);
            string title;
            string filename;
            IReadOnlyList<string> regions = [], languages = [], flags = [];
            string? revision = null;
            // Arcade R3: a MAME item's header (I5's missingRoms) describes the review, not the release; only the queued
            // DAT identity of an AmbiguousGroup item (G5) keeps it.
            var keepHeader = !system.DatSource.IsRomset();
            if (request.Update == true && request.Action != "Dat")
                return ReviewResolutionResult.Invalid("update", ReviewResolutionRequest.UpdateNeedsDat);
            if (request.Disc is not null && !system.FileSets)
                return ReviewResolutionResult.Invalid("disc", "Disc numbers are for disc systems.");
            if (request.Action == "Attach" && request.Flag == "DLC" && system.DatSource != DatSource.TitleDb)
                return ReviewResolutionResult.Invalid("flag", "Only a titledb game can take a DLC.");
            var discFlag = request.Disc is { } disc ? $"Disc {disc}" : null; // B10: the flag NoIntroName.Parse makes from a Redump name
            if (request.Action == "Dat")
            {
                // Arcade R2: a set is identified only by its name and CRCs; a pick would name it after a set it doesn't hold.
                if (system.DatSource.IsRomset())
                    return ReviewResolutionResult.Invalid("action", "Arcade sets can't be resolved from a DAT pick; use Manual or Attach.");
                if (item.Reason != "NoDatMatch")
                    return ReviewResolutionResult.Invalid("action", "DAT selection requires a NoDatMatch item.");
                var chosen = systems.ById(request.DatSystem!);
                if (chosen is null || chosen.Family != system.Family)
                    return ReviewResolutionResult.Invalid("datSystem", "Choose a system in the source's family.");
                // B14: a set's every member must fit the chosen system, as the one file must.
                var stored = set?.Select(m => (Name: m.File.Name, m.Row.SizeBytes)).ToList() ?? [(item.OriginalFileName, item.SizeBytes)];
                if (external && stored.Any(f => !chosen.RomExtensions.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase)
                    || f.SizeBytes < chosen.MinRomSizeBytes || f.SizeBytes > chosen.MaxRomSizeBytes))
                    return ReviewResolutionResult.Invalid("datSystem", "That system can't store this file's extension or size.");
                if (request.Update == true && chosen.DatSource != DatSource.TitleDb)
                    return ReviewResolutionResult.Invalid("update", "Only a titledb game can be stored as an update.");
                system = chosen;
                var store = dats.For(system.Id);
                if (chosen.DatSource == DatSource.TitleDb)
                {
                    // T9: the picked title is named and grouped as T4 would have for its id, with N from the original
                    // file name; titledb never checks hashes, so it is as verified as an automatic match.
                    var pickedId = TitleDb.TitleIdOfGameName(request.DatGameName!);
                    if (request.Update == true && pickedId is not null)
                    {
                        // B8: a base pick becomes its update id; an update pick stays; anything else can't be an update.
                        pickedId = TitleDb.UpdateIdOf(pickedId);
                        if (pickedId is null)
                            return ReviewResolutionResult.Invalid("update", "Pick the base game or one of its updates to store this file as an update.");
                    }
                    var match = store is null || pickedId is null
                        || !store.Roms.Any(r => r.GameName == request.DatGameName)
                        ? null
                        : TitleDb.Match(pickedId, item.OriginalFileName, store.ByTitleId);
                    if (match is null)
                        return ReviewResolutionResult.Invalid("datGameName", "Choose a titledb game whose base game is in the loaded DAT.");
                    title = match.BaseTitle; languages = match.Languages; revision = match.Revision; flags = match.Flags;
                    filename = match.RomFileName;
                    identification = new(IdentificationMethod.TitleDb, store!.DatName, store.Version, match.GameName);
                    metadata = TitleDb.MetadataOf(match.Base);
                }
                else
                {
                    // B3a: a game listed more than once (Atari 7800 .a78 and .bin) uses the ROM with the file's own extension, else the first.
                    var datRom = store?.Roms.FirstOrDefault(r => r.GameName == request.DatGameName
                            && string.Equals(Path.GetExtension(r.RomName), originalExtension, StringComparison.OrdinalIgnoreCase))
                        ?? store?.Roms.FirstOrDefault(r => r.GameName == request.DatGameName);
                    if (datRom is null || store is null)
                        return ReviewResolutionResult.Invalid("datGameName", "Choose a game in the loaded DAT.");
                    // B15: a FileSets game's DAT rom may be a "(Track 1).bin" data track, so its name and tags come from the game name.
                    var parsed = NoIntroName.Parse(chosen.FileSets ? datRom.GameName : Path.GetFileNameWithoutExtension(datRom.RomName));
                    title = parsed.BaseTitle; regions = parsed.Regions; languages = parsed.Languages; revision = parsed.Revision;
                    flags = [.. parsed.Flags, "Unverified"];
                    // G4: the DAT name's own extension (DAT entries are filtered to RomExtensions), e.g. an SMS-mode Game Gear .sms.
                    filename = chosen.FileSets
                        ? datRom.GameName + " (Unverified)" + originalExtension
                        : Path.GetFileNameWithoutExtension(datRom.RomName) + " (Unverified)" + (external || chosen.KeepsExtension(originalExtension) ? originalExtension : Path.GetExtension(datRom.RomName));
                    identification = new(IdentificationMethod.Manual, store.DatName, store.Version, datRom.GameName);
                    metadata = store.MetadataFor(datRom.Crc32);
                }
            }
            else if (request.Action == "Attach" && request.Flag == "None")
            {
                if (item.Reason != "AmbiguousGroup" ||
                    !(JsonSerializer.Deserialize<Guid[]>(item.CandidateGameIds ?? "[]") ?? []).Contains(request.TargetGameId!.Value))
                    return ReviewResolutionResult.Invalid("targetGameId", "Choose one of the ambiguous item's candidates.");
                var dat = JsonNode.Parse(item.DatIdentification!)!;
                var datGameName = dat["datGameName"]!.GetValue<string>();
                filename = Path.GetFileName(dat["romName"]!.GetValue<string>());
                target = request.TargetGameId;
                if (system.DatSource.IsRomset())
                {
                    // Arcade G5: the queued identity and base title; BIOS from the header, no regions, languages or revision.
                    identification = new(IdentificationMethod.Dat, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), datGameName);
                    title = dat["baseTitle"]!.GetValue<string>();
                    var header = item.Header is null ? null : JsonNode.Parse(item.Header);
                    flags = header?["isBios"]?.GetValue<bool>() == true ? ["BIOS"] : [];
                    metadata = header?["setName"]?.GetValue<string>() is { } setName && dats.For(system.Id) is { } mameStore
                        ? mameStore.MetadataForSet(setName) : DatMetadata.Empty;
                    keepHeader = true;
                }
                else if (dat["titleId"] is not null)
                {
                    // T4: the queued titledb identity, whole, so a refresh that drops the entry changes nothing.
                    identification = new(IdentificationMethod.TitleDb, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), datGameName);
                    title = dat["baseTitle"]!.GetValue<string>();
                    regions = Strings(dat["regions"]); languages = Strings(dat["languages"]); flags = Strings(dat["flags"]);
                    revision = dat["revision"]?.GetValue<string>();
                    // T7 from the base entry while the current DAT still has it.
                    metadata = TitleDb.TitleIdOfGameName(datGameName) is { } baseId && dats.For(system.Id)?.ByTitleId(baseId) is { } baseEntry
                        ? TitleDb.MetadataOf(baseEntry) : DatMetadata.Empty;
                }
                else
                {
                    // Keep the queued identity even when the loaded DATs have changed.
                    var method = dat["method"]?.GetValue<string>() == nameof(IdentificationMethod.Serial)
                        ? IdentificationMethod.Serial : IdentificationMethod.Dat;
                    identification = new(method, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), datGameName);
                    // B10: a FileSets release takes its tags from the DAT game name, as ingest does.
                    var tags = NoIntroName.Parse(system.FileSets ? datGameName : Path.GetFileNameWithoutExtension(filename));
                    title = NoIntroName.Parse(datGameName).BaseTitle;
                    regions = tags.Regions; languages = tags.Languages; revision = tags.Revision; flags = tags.Flags;
                    metadata = dats.For(system.Id)?.MetadataFor(item.Crc32) ?? DatMetadata.Empty;
                }
            }
            else if (request.Action == "Attach")
            {
                target = request.TargetGameId;
                var game = await games.GetAsync(target!.Value, ct).ConfigureAwait(false);
                if (game is null)
                    return new("NotFound");
                if (game.SystemId != system.Id)
                    return ReviewResolutionResult.Invalid("targetGameId", "Target must belong to the same system.");
                var (zip, current, error) = pipeline.OpenTarget(game.ZipPath, system.Id, item.Sha1);
                using (zip)
                {
                    if (error is not null)
                        return new(error.Reason);
                    title = current!.Title;
                    if (discFlag is not null && current.Roms.FirstOrDefault(r => r.IsPrimary) is { } primary)
                    {
                        // B11: disc 2 lines up with disc 1.
                        regions = primary.Regions; languages = primary.Languages; revision = primary.Revision;
                    }
                }
                var flag = discFlag ?? request.Flag!;
                var stem = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(item.OriginalFileName));
                if (flag == "Release")
                {
                    // A2: tags from the file's own name (a set's cue, .toc, .ccd or .mds); another disc is Attach as Disc's job. Unverified
                    // keeps DAT-verified releases primary, as the Dat action's manual pick does.
                    var named = set?.FirstOrDefault(m => Path.GetExtension(m.File.Name).ToLowerInvariant() is ".cue" or ".toc" or ".ccd" or ".mds").File?.Name
                        ?? item.OriginalFileName;
                    var tags = NoIntroName.Parse(Path.GetFileNameWithoutExtension(named));
                    regions = tags.Regions; languages = tags.Languages; revision = tags.Revision;
                    flags = [.. tags.Flags.Where(f => !Regex.IsMatch(f, @"^(Disc|Track) \d+$", RegexOptions.IgnoreCase))];
                    if (!flags.Contains("Unverified", StringComparer.OrdinalIgnoreCase))
                        flags = [.. flags, "Unverified"];
                    if (!stem.Contains("(Unverified)", StringComparison.OrdinalIgnoreCase))
                        stem += " (Unverified)";
                }
                else
                {
                    // A DLC is an add-on (not ranked primary, counted as DLC); its source name may already say so.
                    flags = [flag];
                    if ((discFlag is null && flag != "DLC") || !stem.Contains($"({flag})", StringComparison.OrdinalIgnoreCase))
                        stem += $" ({flag})";
                }
                filename = stem + (external || system.KeepsExtension(originalExtension) ? originalExtension : system.RomExtensions[0]);
            }
            else
            {
                title = request.Title!.Trim();
                regions = request.Regions!; languages = request.Languages!; flags = request.Flags!; revision = request.Revision;
                if (discFlag is not null)
                    flags = [.. flags.Where(f => !Regex.IsMatch(f, @"^Disc \d+$", RegexOptions.IgnoreCase)), discFlag];
                var stem = title + (regions.Count == 0 ? "" : $" ({string.Join(", ", regions)})") +
                    (string.IsNullOrEmpty(revision) ? "" : $" ({revision})") + string.Concat(flags.Select(f => $" ({f})"));
                filename = FileNameSanitizer.Sanitize(stem) + (external || system.KeepsExtension(originalExtension) ? originalExtension : system.RomExtensions[0]);
            }

            // B12, and B1 for every system: a name may hold "..", as Redump and No-Intro names do ("...Iru!"); only "." or ".." or a separator is refused.
            // B14: a set keeps its members' source names, so the generated name isn't used.
            if (set is null && (filename is "." or ".." || filename.IndexOfAny(['/', '\\']) >= 0))
                return ReviewResolutionResult.Invalid("title", "The generated ROM filename must not be '.' or '..' or contain '/' or '\\'; use Manual with another title.");
            if (external && !system.FileSets)
                filename = ExternalRomName.AllocateLogicalName(filename, new ExternalNameReservations([])); // E3: portable and within budget
            if (target is { } targetId)
            {
                var game = await games.GetAsync(targetId, ct).ConfigureAwait(false);
                if (game is null)
                    return new("NotFound");
                if (game.SystemId != system.Id)
                    return ReviewResolutionResult.Invalid("targetGameId", "Target must belong to the same system.");
            }
            if (set is not null)
                return await ExternalPreparation.ResolveSetAsync(set, system, request.Action!, title, regions, languages, revision, flags, identification, metadata,
                    target, actor, userId, progress).ConfigureAwait(false);
            if (external)
                return await ExternalPreparation.ResolveExternalAsync(id, item, file, preparedSnapshot!, system, request.Action!, filename, title, regions,
                    languages, revision, flags, identification, metadata, target, actor, userId, progress,
                    keepHeader && item.Header is not null ? JsonNode.Parse(item.Header) as JsonObject : null).ConfigureAwait(false);
            if (item.ArchivePath is not null)
            {
                var family = systems.ByFamily(systems.ById(item.SystemId)!.Family);
                var entries = extractor.Extract(source, work, ext => family.FirstOrDefault(h => h.RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.MaxRomSizeBytes);
                var entry = entries.FirstOrDefault(e => e.EntryPath == item.ArchiveEntry);
                if (entry is null)
                    return new("VerifyFailed");
                file = file with { FullPath = entry.ExtractedPath, ArchivePath = source, ArchiveEntry = item.ArchiveEntry };
            }
            // The handle is closed on return, before the pipeline deletes the source (Windows FileShare semantics).
            var read = await BoundedRomRead.ReadAsync(file.FullPath, system.MinRomSizeBytes, system.MaxRomSizeBytes,
                pipeline.WrapEmbeddedReadStream, ct).ConfigureAwait(false);
            if (read.Status != BoundedReadStatus.Read)
                return new("VerifyFailed");
            var bytes = BoundedRomRead.Normalize(system, read.Bytes!);
            if (Convert.ToHexStringLower(SHA1.HashData(bytes)) != item.Sha1)
                return new("VerifyFailed");
            var now = time.GetUtcNow();
            var rom = new InfoJsonRom
            {
                FileName = filename, OriginalFileName = item.OriginalFileName, BaseTitle = title,
                Regions = regions, Languages = languages, Revision = revision, Flags = flags, IsPrimary = false,
                Version = ReleaseVersion.For(flags, item.OriginalFileName),
                Identification = identification, Header = keepHeader && item.Header is not null ? JsonNode.Parse(item.Header) as JsonObject : null,
                Metadata = new InfoJsonRomMetadata(bytes.Length, item.Crc32, item.Md5, item.Sha1, now, now, file.SourcePath),
            };
            var result = await pipeline.StoreReleaseAsync(file, system, rom, metadata, bytes, target,
                gameId => reviews.SetResolutionAsync(id, "Resolved", gameId, userId, time.GetUtcNow().UtcDateTime, ct),
                actor, request.Action, ct).ConfigureAwait(false);
            return result.Outcome == IngestOutcome.Ingested ? new(GameId: result.GameId) : new(result.Reason);
        }
        catch (ArchiveRejectedException ex)
        {
            logger.LogWarning(ex, "Review {Id}: archive could not be re-extracted", id);
            return new("VerifyFailed");
        }
        catch (LimitExceededException) { return new("VerifyFailed"); }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError(ex, "Review {Id}: DB write failed", id);
            return new("DbWriteFailed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchiveOutputException)
        {
            logger.LogError(ex, "Review {Id}: IO failure", id);
            return new("IoError");
        }
        finally
        {
            // Never walk into a replacement of the work folder.
            if (ExternalRomPath.ClassifyDirectory(paths.Value.Cache, work).Status == ExternalPathStatus.Found)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Review {Id}: couldn't remove work directory {Path}", id, work);
                }
            }
        }
    }

    private static IReadOnlyList<string> Strings(JsonNode? array) => array?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
}
