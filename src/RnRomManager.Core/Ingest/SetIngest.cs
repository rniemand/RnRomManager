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

/// <summary>Ingests a multi-file set (a main file and its members) as one release, for <see cref="IngestPipeline"/>.</summary>
internal sealed class SetIngest(
    IngestPipeline pipeline,
    DatCache dats,
    ISystemRegistry systems,
    IRomRepository roms,
    IGameRepository games,
    IIntegrityIssueRepository issues,
    IIngestReviewRepository reviews,
    IOptions<PathsOptions> paths,
    TimeProvider time)
{
    internal async Task<IReadOnlyList<IngestItemResult>> ProcessSetCoreAsync(
        IReadOnlyList<IngestFile> members,
        Guid jobId,
        AuditActor actor,
        CircuitBreaker breaker,
        JobProgress? progress)
    {
        // I2: a started set runs to the end.
        var cancellationToken = CancellationToken.None;
        var system = systems.ById(members[0].SystemId) ?? throw new InvalidOperationException($"No handler for ingest folder '{members[0].SystemId}'.");
        var root = paths.Value.Ingest;
        IReadOnlyList<IngestItemResult> All(IngestItemResult result) => members.Select(_ => result).ToList();
        var tooRecent = new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: system.Id);

        // I1/I2 for each member: checked and unchanged since the listing; one TooRecent member skips the whole set.
        var checkedFiles = new List<ExternalPathCheck>();
        foreach (var file in members)
        {
            var fresh = ExternalRomPath.ClassifyFile(root, file.FullPath);
            if (fresh.Status != ExternalPathStatus.Found)
                return All(IngestItemResult.Error(IngestErrors.IoError, $"{file.Name}: {fresh.Details}", system.Id));
            if (fresh.Snapshot!.Length != file.SizeBytes || fresh.Snapshot.LastWriteTimeUtc != file.ModifiedAtUtc
                || time.GetUtcNow().UtcDateTime - fresh.Snapshot.LastWriteTimeUtc < IngestJob.MinFileAge)
                return All(tooRecent);
            checkedFiles.Add(fresh);
        }

        // Step 1 per file: a file outside the size limits fails as today; the rest of the set waits for it.
        string? SizeProblem(long size) => size < system.MinRomSizeBytes ? IngestFailures.TooSmall : size > system.MaxRomSizeBytes ? IngestFailures.TooLarge : null;
        if (checkedFiles.Any(c => SizeProblem(c.Snapshot!.Length) is not null))
        {
            return Enumerable.Range(0, members.Count).Select(i =>
            {
                var size = checkedFiles[i].Snapshot!.Length;
                return SizeProblem(size) is { } problem
                    ? pipeline.IngestFileMoves.FailFile(members[i], actor, problem, $"{size} bytes; {system.Id} files are {system.MinRomSizeBytes}–{system.MaxRomSizeBytes} bytes", progress: progress)
                    : new IngestItemResult(IngestOutcome.Skipped, IngestReason.IncompleteSet, "another file of the set is outside the size limits", system.Id);
            }).ToList();
        }

        // Step 2 for each member: hashed once, in one streamed read.
        var hashed = new List<(IngestFile File, ExternalSource Source, RomFileHashes Hashes)>();
        for (var i = 0; i < members.Count; i++)
        {
            var (file, fresh) = (members[i], checkedFiles[i]);
            progress?.SetCurrentItem(file.Name);
            var (outcome, hashes, failure) = await pipeline.ExternalIngest.HashCheckedAsync(file, root, fresh.Path, fresh.Snapshot!, system.MaxRomSizeBytes, recheckPath: true,
                cancellationToken, progress).ConfigureAwait(false);
            if (outcome == HashOutcome.Failed)
                return All(IngestItemResult.Error(IngestErrors.IoError, $"{file.Name}: {failure}", system.Id));
            if (outcome == HashOutcome.Changed)
                return All(tooRecent);
            hashed.Add((file, new ExternalSource(root, fresh.Path, fresh.Snapshot!, progress), hashes!));
        }

        // A member that changed after it was hashed skips the whole set before anything moves.
        if (hashed.Any(m => ExternalRomPath.ClassifyFile(root, m.Source.Path).Snapshot != m.Source.Snapshot))
            return All(tooRecent);

        RomHashes Hashes(int i) => new(hashed[i].Hashes.Crc32, hashed[i].Hashes.Md5, hashed[i].Hashes.Sha1);
        IReadOnlyList<IngestItemResult> Each(Func<int, IngestItemResult> result) => Enumerable.Range(0, members.Count).Select(result).ToList();

        // B7a: the data track is the one member the DAT lists.
        var store = dats.For(system.Id);
        var matches = Enumerable.Range(0, hashed.Count).Where(i => store?.BySha1(hashed[i].Hashes.Sha1) is not null).ToList();
        if (matches.Count != 1)
        {
            var reason = matches.Count == 0 ? "NoDatMatch" : ReviewReasons.AmbiguousSet;
            var queued = new List<IngestItemResult>();
            for (var i = 0; i < hashed.Count; i++)
            {
                var (file, _, hashes) = hashed[i];
                // ingest_review_items.sha1 is unique: another source's Pending row (an earlier member included) keeps this one out.
                if (await reviews.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false) is { Status: "Pending" } pending
                    && pending.SourcePath != file.SourcePath)
                {
                    queued.Add(new IngestItemResult(IngestOutcome.Skipped, IngestReason.AlreadyInReview, $"same as pending review source {pending.SourcePath}",
                        system.Id, Sha1: hashes.Sha1));
                    continue;
                }
                // An AmbiguousSet item can't be resolved, so it gets no suggestions.
                IReadOnlyList<Suggestion> suggestions = matches.Count == 0 ? SuggestionEngine.Find(store, null, file.Name, hashes.SizeBytes) : [];
                queued.Add(await pipeline.IngestFileMoves.QueueReviewAsync(file, file.Name, system.Id, reason, Hashes(i), hashes.SizeBytes, system.ReadHeader(hashes.Header), jobId,
                    suggestions, null, null, cancellationToken).ConfigureAwait(false));
            }
            return queued;
        }

        var data = matches[0];
        var (dataFile, dataSource, dataHashes) = hashed[data];

        // Step 3 for the data track only: a library duplicate or another source's Pending row sends every member to
        // _duplicates/; a Dismissed row follows the file, which is then ingested.
        IReadOnlyList<IngestItemResult> AllDuplicates(string details) => Each(i =>
            pipeline.IngestFileMoves.MoveToDuplicates(members[i], actor, progress)
            ?? new IngestItemResult(IngestOutcome.Duplicate, Details: details, SystemId: system.Id, Sha1: hashed[i].Hashes.Sha1));
        if (await roms.GetBySha1Async(dataHashes.Sha1, cancellationToken).ConfigureAwait(false) is { } known)
            return await DuplicateSetAsync(hashed, data, known, actor, progress, cancellationToken).ConfigureAwait(false);
        var review = await reviews.GetBySha1Async(dataHashes.Sha1, cancellationToken).ConfigureAwait(false);
        if (review is { Status: "Pending" } && review.SourcePath != dataFile.SourcePath)
            return AllDuplicates($"the data track is the same as pending review source {review.SourcePath}");
        if (review is { Status: "Dismissed" })
            await reviews.UpsertAsync(review with
            {
                SourcePath = dataFile.SourcePath, ArchivePath = null, ArchiveEntry = null, OriginalFileName = dataFile.Name, LastSeenJobId = jobId,
            }, cancellationToken).ConfigureAwait(false);
        var release = IngestIdentifier.LibretroRelease(store!.BySha1(dataHashes.Sha1)!, store, dataFile, external: true, tagsFromGameName: true);
        var owners = await roms.ListGameIdsByBaseTitleAsync(system.Id, release.BaseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
            return Each(i => new IngestItemResult(IngestOutcome.Skipped, IngestReason.AmbiguousGroup, $"games {string.Join(", ", owners)} own '{release.BaseTitle}'",
                system.Id, Sha1: hashed[i].Hashes.Sha1));

        var now = time.GetUtcNow();
        var rom = SetRom(new InfoJsonRom
        {
            FileName = dataFile.Name,
            OriginalFileName = dataFile.Name,
            BaseTitle = release.BaseTitle,
            Regions = release.Regions,
            Languages = release.Languages,
            Revision = release.Revision,
            Flags = release.Flags,
            IsPrimary = false,
            Identification = new InfoJsonIdentification(release.Method, store.DatName, store.Version, release.GameName),
            Metadata = null!, // SetRom fills it
        }, system, hashed, data, now);
        var set = SetPlacements(hashed);
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: dataHashes.Sha1);
        var stored = owners.Count == 0
            ? await pipeline.IngestStore.NewGameAsync(dataFile, system, rom, release.Metadata, null, breaker, result, now, actor, null, cancellationToken, source: dataSource, set: set)
                .ConfigureAwait(false)
            : await pipeline.IngestStore.AddToGameAsync(dataFile, system, owners[0], rom, release.Metadata, null, breaker, result, now, actor, null, cancellationToken,
                source: dataSource, set: set).ConfigureAwait(false);
        // One outcome for the whole set; each member's item carries its own SHA1.
        return Each(i => stored with { Sha1 = hashed[i].Hashes.Sha1 });
    }

    /// <summary>
    /// B10: completes a set's release from its hashed members: every member keeps its source name, the member at
    /// <paramref name="main"/> is the release's main file and the rest go in its <c>files</c>.
    /// </summary>
    internal static InfoJsonRom SetRom(
        InfoJsonRom rom,
        ISystemHandler system,
        List<(IngestFile File, ExternalSource Source, RomFileHashes Hashes)> hashed,
        int main,
        DateTimeOffset now)
    {
        var (file, _, hashes) = hashed[main];
        return rom with
        {
            FileName = file.Name,
            OriginalFileName = file.Name,
            Version = ReleaseVersion.For(rom.Flags, file.Name),
            Header = system.ReadHeader(hashes.Header) is { } header ? JsonSerializer.SerializeToNode(header.Fields) as JsonObject : null,
            Metadata = new InfoJsonRomMetadata(hashes.SizeBytes, hashes.Crc32, hashes.Md5, hashes.Sha1, now, now, file.SourcePath),
            Files = hashed.Where((_, i) => i != main)
                .Select(m => new InfoJsonRomFile(m.File.Name, m.File.Name, m.File.Name, m.Hashes.SizeBytes, m.Hashes.Crc32, m.Hashes.Md5, m.Hashes.Sha1))
                .ToList(),
        };
    }

    internal static List<SetPlacement> SetPlacements(List<(IngestFile File, ExternalSource Source, RomFileHashes Hashes)> hashed) =>
        hashed.Select(m => new SetPlacement(m.File, new ExternalPlacement(m.Source, m.File.Name), m.Hashes.Sha1)).ToList();

    /// <summary>
    /// B14: stores an unmatched cue, TOC, CloneCD or MDS set resolved in Review as one release, all or nothing. <paramref name="members"/>
    /// are in set order (the cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c>, then the files it names, or <c>.img</c> and <c>.sub</c>, or the <c>.mdf</c>), each with its review row's SHA1;
    /// the first file named, the <c>.img</c> or the <c>.mdf</c> (<c>members[1]</c>) is the main file. <paramref name="rom"/> carries what the action decided (title, tags,
    /// identification); its names, version, header, metadata and <c>files</c> come from the hashed members. Nothing is
    /// moved to <c>_failed/</c>: a refusal leaves every file where it is.
    /// </summary>
    /// <returns><c>Ingested</c>, or an error: <c>Duplicate</c> (the main file is already a release), <c>IoError</c>,
    /// <c>VerifyFailed</c> (a SHA1 differs, a member is over the size limit or changed), <c>AmbiguousGroup</c>,
    /// <c>NameTaken</c>/<c>NameTooLong</c>, or the placement's and mirror's codes.</returns>
    internal async Task<IngestItemResult> StoreSetReleaseAsync(
        IReadOnlyList<(IngestFile File, string Sha1)> members,
        ISystemHandler system,
        InfoJsonRom rom,
        DatMetadata metadata,
        Guid? targetGameId,
        Func<Guid, Task> afterMirror,
        AuditActor actor,
        string reviewAction,
        JobProgress progress)
    {
        var cancellationToken = CancellationToken.None; // like a single-file resolve, it runs to the end
        const int main = 1;
        var root = paths.Value.Ingest;
        var mainSha1 = members[main].Sha1;
        IngestItemResult Fail(string code, string details) => IngestItemResult.Error(code, details, system.Id, mainSha1);

        // roms.sha1 is unique: a main file already in the library would only fail at the mirror, after the moves.
        if (await roms.GetBySha1Async(mainSha1, cancellationToken).ConfigureAwait(false) is { } known)
            return Fail("Duplicate", $"{members[main].File.Name} is already a release of game {known.GameId}");

        var hashed = new List<(IngestFile File, ExternalSource Source, RomFileHashes Hashes)>();
        foreach (var (file, sha1) in members)
        {
            progress.SetCurrentItem(file.Name);
            var fresh = ExternalRomPath.ClassifyFile(root, file.FullPath);
            if (fresh.Status != ExternalPathStatus.Found)
                return Fail(IngestErrors.IoError, $"{file.Name}: {fresh.Details}");
            var (outcome, hashes, failure) = await pipeline.ExternalIngest.HashCheckedAsync(file, root, fresh.Path, fresh.Snapshot!, system.MaxRomSizeBytes, recheckPath: true,
                cancellationToken, progress).ConfigureAwait(false);
            if (outcome == HashOutcome.Failed)
                return Fail(IngestErrors.IoError, $"{file.Name}: {failure}");
            // Changed covers a file over the size limit too: its hash stops at MaxRomSizeBytes.
            if (outcome == HashOutcome.Changed || hashes!.Sha1 != sha1)
                return Fail(IngestErrors.VerifyFailed, failure ?? $"{file.Name}: sha1 {hashes!.Sha1}, not {sha1}");
            progress.ItemDone();
            hashed.Add((file, new ExternalSource(root, fresh.Path, fresh.Snapshot!, progress), hashes));
        }
        if (hashed.Any(m => ExternalRomPath.ClassifyFile(root, m.Source.Path).Snapshot != m.Source.Snapshot))
            return Fail(IngestErrors.VerifyFailed, "a file of the set changed after it was hashed");

        var owners = targetGameId is { } target ? [target] :
            await roms.ListGameIdsByBaseTitleAsync(system.Id, rom.BaseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
            return Fail("AmbiguousGroup", "Several games own this base title; use Attach.");
        var now = time.GetUtcNow();
        rom = SetRom(rom, system, hashed, main, now);
        var (mainFile, mainSource, _) = hashed[main];
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: mainSha1);
        var stored = owners.Count == 0
            ? await pipeline.IngestStore.NewGameAsync(mainFile, system, rom, metadata, null, new CircuitBreaker(), result, now, actor, reviewAction, cancellationToken,
                afterMirror, mainSource, SetPlacements(hashed)).ConfigureAwait(false)
            : await pipeline.IngestStore.AddToGameAsync(mainFile, system, owners[0], rom, metadata, null, new CircuitBreaker(), result, now, actor, reviewAction,
                cancellationToken, afterMirror, mainSource, SetPlacements(hashed)).ConfigureAwait(false);
        // As for a single file: a member that changed before it could be moved is a verification failure, not a skip.
        return stored.Outcome == IngestOutcome.Skipped
            ? Fail(IngestErrors.VerifyFailed, "a file of the set changed before it could be moved")
            : stored;
    }

    /// <summary>
    /// B14: the set's data track is already release <paramref name="known"/>. The data track repairs itself as a single
    /// file does; each other member whose name and SHA1 are a file of that release with an open self-repairable issue is
    /// put back the same way (<c>Repaired</c>), one at a time; every other member is a <c>Duplicate</c> in <c>_duplicates/</c>.
    /// </summary>
    internal async Task<IReadOnlyList<IngestItemResult>> DuplicateSetAsync(
        List<(IngestFile File, ExternalSource Source, RomFileHashes Hashes)> hashed,
        int data,
        RomRow known,
        AuditActor actor,
        JobProgress? progress,
        CancellationToken cancellationToken)
    {
        var results = new IngestItemResult[hashed.Count];
        var (dataFile, dataSource, dataHashes) = hashed[data];
        results[data] = await pipeline.IngestStore.DuplicateOrRepairAsync(dataFile, known, null, dataSource, dataHashes.SizeBytes, dataHashes.Sha1, actor, cancellationToken)
            .ConfigureAwait(false);
        var game = await games.GetAsync(known.GameId, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < hashed.Count; i++)
        {
            if (i == data)
                continue;
            var (file, source, hashes) = hashed[i];
            var own = (await roms.ListFilesBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(f => f.RomId == known.Id && string.Equals(f.ExternalFileName, file.Name, StringComparison.OrdinalIgnoreCase));
            if (game is not null && own is not null && await OpenSetFileIssuesAsync(game, own, cancellationToken).ConfigureAwait(false) is { Count: > 0 } open)
            {
                results[i] = await pipeline.IngestStore.RepairExternalAsync(file, known, game, open, source, hashes.Sha1, actor, cancellationToken, setFile: own.ExternalFileName)
                    .ConfigureAwait(false);
                continue;
            }
            results[i] = pipeline.IngestFileMoves.MoveToDuplicates(file, actor, progress) ?? new IngestItemResult(IngestOutcome.Duplicate,
                Details: $"a file of the set of {game?.ZipPath} › {known.ExternalFileName}", SystemId: known.SystemId, GameId: known.GameId, Sha1: hashes.Sha1);
        }
        return results;
    }

    /// <summary>B14: the open self-repairable issues of exactly this set file of this game (keyed like a main file's, B19).</summary>
    internal async Task<IReadOnlyList<OpenIssue>> OpenSetFileIssuesAsync(GameRow game, RomFileRow file, CancellationToken cancellationToken) =>
        [.. (await issues.ListOpenExternalAsync(game.ZipPath, file.ExternalFileName, IntegrityIssueType.SelfRepairable, cancellationToken).ConfigureAwait(false))
            .Where(i => string.Equals(i.ZipPath, game.ZipPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.EntryName, file.ExternalFileName, StringComparison.OrdinalIgnoreCase)
                && (i.GameId is null || i.GameId == file.GameId))
            .Select(i => new OpenIssue(i.Id, i.Type))];

    /// <summary>
    /// B14 for a lone <c>.bin</c> (one no cue names): repaired when exactly one set file has its name and SHA1 and an open
    /// self-repairable issue; null otherwise, and step 3 goes on as today.
    /// </summary>
    internal async Task<IngestItemResult?> RepairLoneTrackAsync(
        IngestFile file,
        ExternalSource source,
        string sha1,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        var repairable = new List<(RomFileRow File, GameRow Game, IReadOnlyList<OpenIssue> Open)>();
        foreach (var candidate in (await roms.ListFilesBySha1Async(sha1, cancellationToken).ConfigureAwait(false))
            .Where(f => string.Equals(f.ExternalFileName, file.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (await games.GetAsync(candidate.GameId, cancellationToken).ConfigureAwait(false) is { } game
                && await OpenSetFileIssuesAsync(game, candidate, cancellationToken).ConfigureAwait(false) is { Count: > 0 } open)
                repairable.Add((candidate, game, open));
        }
        if (repairable.Count != 1)
            return null;
        var (setFile, owner, issuesOpen) = repairable[0];
        if (await roms.GetAsync(setFile.GameId, setFile.RomId, cancellationToken).ConfigureAwait(false) is not { } release)
            return null;
        return await pipeline.IngestStore.RepairExternalAsync(file, release, owner, issuesOpen, source, sha1, actor, cancellationToken, setFile: setFile.ExternalFileName)
            .ConfigureAwait(false);
    }
}
