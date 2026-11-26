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

/// <summary>Identifies a hashed source by DAT (No-Intro, libretro, MAME) and groups it into a game, for <see cref="IngestPipeline"/>.</summary>
internal sealed class IngestIdentifier(
    IngestPipeline pipeline,
    DatCache dats,
    IRomRepository roms,
    IIngestReviewRepository reviews,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    /// <summary>Steps 3–6 on hashed input.</summary>
    /// <param name="candidates">The handlers that may claim it: the whole family, or the eligible members for an external system.</param>
    /// <param name="headerBytes">What <c>ReadHeader</c> gets: the whole ROM, or its first MiB for an external one.</param>
    /// <param name="bytes">The ROM to store; null for an external ROM, which is never held in memory.</param>
    /// <param name="source">The checked file an external ROM is moved from; null for an embedded ROM.</param>
    /// <param name="mameEntries">Arcade I2: the set ZIP's files, for a <c>Mame</c> candidate; null for every other system.</param>
    internal async Task<IngestItemResult?> IdentifyAsync(
        IngestFile file,
        ISystemHandler folder,
        IReadOnlyList<ISystemHandler> candidates,
        RomHashes hashes,
        long size,
        byte[] headerBytes,
        byte[]? bytes,
        ExternalSource? source,
        Guid jobId,
        AuditActor actor,
        CircuitBreaker breaker,
        CancellationToken cancellationToken,
        IReadOnlyList<MameEntry>? mameEntries = null)
    {
        var external = source is not null;
        // Step 3: global dedupe, self-repair, and review-item lifecycle.
        var review = await reviews.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false);
        if (await roms.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false) is { } known)
        {
            var duplicate = await pipeline.IngestStore.DuplicateOrRepairAsync(file, known, bytes, source, size, hashes.Sha1, actor, cancellationToken).ConfigureAwait(false);
            return duplicate with { ResolvedArchiveDuplicate = duplicate.Outcome == IngestOutcome.Duplicate &&
                file.ArchivePath is not null && review is { Status: "Resolved" } && review.ArchivePath == file.ArchivePath };
        }
        if (source is not null && folder.FileSets && file.ArchiveEntry is null && file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            && await pipeline.SetIngest.RepairLoneTrackAsync(file, source, hashes.Sha1, actor, cancellationToken).ConfigureAwait(false) is { } trackRepaired)
            return trackRepaired;
        if (review is { Status: "Pending" } && review.SourcePath != file.SourcePath)
        {
            logger.LogWarning("{Path}: duplicate of pending review source {ReviewPath} (crc32 {Crc32}, md5 {Md5}, sha1 {Sha1})",
                file.SourcePath, review.SourcePath, hashes.Crc32, hashes.Md5, hashes.Sha1);
            return (file.ArchiveEntry is null ? pipeline.IngestFileMoves.MoveToDuplicates(file, actor, source?.Progress) : null) ??
                new IngestItemResult(IngestOutcome.Duplicate, Details: $"same as pending review source {review.SourcePath}", SystemId: review.SystemId, Sha1: hashes.Sha1);
        }
        if (review is { Status: "Dismissed" } && file.ArchivePath is not null && review.ArchivePath == file.ArchivePath)
            return pipeline.IngestFileMoves.FailFile(file, actor, "Dismissed", "this entry was dismissed from this archive", hashes.Sha1, source?.Progress);
        if (review is { Status: "Dismissed" } && file.ArchivePath is null)
            await reviews.UpsertAsync(review with
            {
                SourcePath = file.SourcePath, ArchivePath = null, ArchiveEntry = null, OriginalFileName = file.Name,
                LastSeenJobId = jobId,
            }, cancellationToken).ConfigureAwait(false);

        // Step 4: header from every handler of the family; the DAT match decides the system (D23).
        var headers = candidates.ToDictionary(h => h.Id, h => h.ReadHeader(headerBytes));
        // T4: a TitleDb system matches by title id (header, else file name); the others by SHA1.
        var match = candidates
            .Where(h => !h.DatSource.IsRomset()) // Arcade I4: matched by set name and CRCs below
            .Select(h => (Handler: h, Store: dats.For(h.Id)))
            .Select(x => (x.Handler, x.Store, Rom: x.Store?.BySha1(hashes.Sha1),
                Title: x.Handler.DatSource == DatSource.TitleDb && x.Store is { } titles
                    && TitleDb.TitleIdFrom(headers[x.Handler.Id]?.Fields, file.Name) is { } titleId
                    ? TitleDb.Match(titleId, file.Name, titles.ByTitleId) : null))
            .FirstOrDefault(x => x.Rom is not null || x.Title is not null);
        var method = IdentificationMethod.Dat;
        if (match.Rom is null && match.Title is null &&
            Path.GetExtension(file.Name).Equals(".wbfs", StringComparison.OrdinalIgnoreCase) &&
            candidates.FirstOrDefault(h => h.Id == "wii") is { } wii && dats.For(wii.Id) is { } wiiDat &&
            MatchWbfs(wiiDat, headers[wii.Id]) is { } serialRom)
        {
            match = (wii, wiiDat, serialRom, null);
            method = IdentificationMethod.Serial;
        }
        // Arcade I4/I5: a set ZIP matches when its stem names a DAT set and every required ROM CRC is in it.
        MameSet? mameSet = null;
        MameMatch? mameMatch = null;
        if (match.Rom is null && match.Title is null && mameEntries is not null
            && candidates.FirstOrDefault(h => h.DatSource.IsRomset()) is { } arcade && dats.For(arcade.Id) is { } mameStore)
        {
            mameSet = mameStore.BySetName(Path.GetFileNameWithoutExtension(file.Name));
            mameMatch = mameSet is null ? null : MameMatcher.Match(mameSet, mameEntries);
            if (mameMatch is not { Complete: true })
            {
                var missing = mameSet is null ? null : new RomHeader(null, null, true, new Dictionary<string, object?>
                {
                    ["setName"] = mameSet.Name,
                    ["description"] = mameSet.Description,
                    ["missingRoms"] = MissingRomList(mameMatch!.Missing),
                });
                return await pipeline.IngestFileMoves.QueueReviewAsync(file, file.Name, arcade.Id, "NoDatMatch", hashes, size, missing, jobId, [], null, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            match = (arcade, mameStore, null, null);
        }
        if (match.Rom is null && match.Title is null && mameSet is null)
        {
            // Step 5. Every ReadHeader null (headerless system) counts as plausible.
            var found = headers.Values.FirstOrDefault(h => h is not null);
            if (found is { IsPlausible: false })
                return pipeline.IngestFileMoves.FailFile(file, actor, IngestFailures.InvalidHeader, "no DAT match, and the header isn't valid for this system", hashes.Sha1, source?.Progress);
            // I3: an external header naming an ineligible handler falls back to the folder's handler, or the first eligible one.
            var classified = candidates.FirstOrDefault(h => h.Id == found?.ClassifiedSystemId)
                ?? (external ? candidates.FirstOrDefault(h => h.Id == folder.Id) ?? candidates[0] : folder);
            var originalName = file.Name;
            if (file.ArchiveEntry is null && !external)
                file = pipeline.IngestFileMoves.RenameToClassifiedExtension(file, found, candidates, actor);
            return await pipeline.IngestFileMoves.QueueReviewAsync(file, originalName, classified.Id, "NoDatMatch", hashes, size, found, jobId,
                SuggestionEngine.Find(dats.For(classified.Id), found?.Title, originalName, size), null, null, cancellationToken).ConfigureAwait(false);
        }
        var (system, store) = (match.Handler, match.Store!);
        // B7: a lone .bin matching a multi-track disc's data track waits for its cue and other tracks.
        if (system.FileSets && match.Rom is { } track && file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && TrackTag.IsMatch(track.RomName))
            return new IngestItemResult(IngestOutcome.Skipped, IngestReason.IncompleteSet,
                $"the data track of '{track.GameName}'; waiting for its cue and other tracks", system.Id, Sha1: hashes.Sha1);
        if (system.Id != file.SystemId)
            logger.LogInformation("{Path}: dropped in ingest/{Folder} but it's a {System} ROM; stored under roms/{System}", file.FullPath, file.SystemId, system.Id, system.Id);

        var release = match.Title is { } title
            ? new Release(title.RomFileName, title.BaseTitle, title.GameName, [], title.Languages, title.Revision, title.Flags,
                IdentificationMethod.TitleDb, TitleDb.MetadataOf(title.Base), title.TitleId)
            : mameSet is not null
                ? MameRelease(mameSet, store, system.SourceExtension(file.Name)!)
                : LibretroRelease(match.Rom!, store, file, external, match.Handler.FileSets) with { Method = method };
        // I3/E3: an external release's name is a portable logical name within budget. B10: a FileSets name is kept exactly.
        var romName = external && !system.FileSets ? ExternalRomName.AllocateLogicalName(release.FileName, new ExternalNameReservations([])) : release.FileName;

        // Step 6: group by system + base title.
        var baseTitle = release.BaseTitle;
        var owners = await roms.ListGameIdsByBaseTitleAsync(system.Id, baseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1 && system.FileSets && file.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            // B7: a .bin review item can't be resolved (B15), so a lone .bin waits like a set's data track.
            return new IngestItemResult(IngestOutcome.Skipped, IngestReason.AmbiguousGroup, $"games {string.Join(", ", owners)} own '{baseTitle}'",
                system.Id, Sha1: hashes.Sha1);
        }
        if (owners.Count > 1)
        {
            // T4: a TitleDb row also keeps the whole identity, so Attach doesn't depend on the DAT loaded later.
            // Arcade G5: a MAME release keeps its base title (the parent's) and its G2 header for Attach.
            object queued = mameSet is not null ? new { datName = store.DatName, datVersion = store.Version, datGameName = release.GameName, romName, baseTitle } : release.Method switch
            {
                IdentificationMethod.Serial => new { datName = store.DatName, datVersion = store.Version, datGameName = release.GameName, romName, method = nameof(IdentificationMethod.Serial) },
                IdentificationMethod.TitleDb => new
                {
                    datName = store.DatName, datVersion = store.Version, datGameName = release.GameName, romName, titleId = release.TitleId,
                    baseTitle, regions = release.Regions, languages = release.Languages, revision = release.Revision, flags = release.Flags,
                },
                _ => new { datName = store.DatName, datVersion = store.Version, datGameName = release.GameName, romName },
            };
            return await pipeline.IngestFileMoves.QueueReviewAsync(file, file.Name, system.Id, "AmbiguousGroup", hashes, size,
                mameSet is not null ? MameHeader(mameSet, mameMatch!) : headers[system.Id], jobId, [], queued, owners, cancellationToken).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        var rom = new InfoJsonRom
        {
            FileName = romName,
            OriginalFileName = file.Name,
            BaseTitle = baseTitle,
            Regions = release.Regions,
            Languages = release.Languages,
            Revision = release.Revision,
            Flags = release.Flags,
            Version = ReleaseVersion.For(release.Flags, file.Name),
            IsPrimary = false,
            Identification = new InfoJsonIdentification(release.Method, store.DatName, store.Version, release.GameName),
            Header = (mameSet is not null ? MameHeader(mameSet, mameMatch!) : headers[system.Id]) is { } header
                ? JsonSerializer.SerializeToNode(header.Fields) as JsonObject : null,
            Metadata = new InfoJsonRomMetadata(size, hashes.Crc32, hashes.Md5, hashes.Sha1, now, now, file.SourcePath),
        };
        var metadata = release.Metadata;
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: hashes.Sha1);

        return owners.Count == 0
            ? await pipeline.IngestStore.NewGameAsync(file, system, rom, metadata, bytes, breaker, result, now, actor, null, cancellationToken, source: source).ConfigureAwait(false)
            : await pipeline.IngestStore.AddToGameAsync(file, system, owners[0], rom, metadata, bytes, breaker, result, now, actor, null, cancellationToken, source: source).ConfigureAwait(false);
    }

    /// <summary>B3: a Redump rom name's track tag, e.g. <c> (Track 1)</c> or <c> (Track 02)</c>.</summary>
    internal static readonly System.Text.RegularExpressions.Regex TrackTag = new(@" \(Track \d+\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static DatRom? MatchWbfs(DatStore store, RomHeader? header)
    {
        if (header is not { IsPlausible: true, ClassifiedSystemId: "wii" } ||
            header.Fields.GetValueOrDefault("container") is not "wbfs" ||
            header.Fields.GetValueOrDefault("gameId") is not string { Length: 6 } gameId ||
            header.Fields.GetValueOrDefault("discNumber") is not byte disc ||
            header.Fields.GetValueOrDefault("version") is not byte version)
            return null;

        var discTag = FormattableString.Invariant($"Disc {disc + 1}");
        var revision = version == 0 ? null : FormattableString.Invariant($"Rev {version}");
        var matches = store.BySerialCode(gameId[..4]).Where(rom =>
        {
            var tags = NoIntroName.Parse(rom.GameName);
            return tags.Revision == revision && (tags.Flags.Contains(discTag) ||
                disc == 0 && !tags.Flags.Any(IsDiscNumber));
        }).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool IsDiscNumber(string flag) =>
        flag.StartsWith("Disc ", StringComparison.Ordinal) && int.TryParse(flag.AsSpan(5),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// I3: an external release keeps its source's extension (lower-cased) on the DAT stem. B10: a <c>FileSets</c>
    /// release takes its tags from the DAT game name, so a data track gets no <c>Track 1</c> flag.
    /// </summary>
    internal static Release LibretroRelease(
        DatRom datRom,
        DatStore store,
        IngestFile file,
        bool external,
        bool tagsFromGameName)
    {
        var tags = NoIntroName.Parse(tagsFromGameName ? datRom.GameName : Path.GetFileNameWithoutExtension(datRom.RomName));
        var fileName = external ? Path.GetFileNameWithoutExtension(datRom.RomName) + Path.GetExtension(file.Name).ToLowerInvariant() : datRom.RomName;
        return new Release(fileName, NoIntroName.Parse(datRom.GameName).BaseTitle, datRom.GameName, tags.Regions, tags.Languages, tags.Revision,
            tags.Flags, IdentificationMethod.Dat, store.MetadataFor(datRom.Crc32), null);
    }

    /// <summary>
    /// Arcade G1/G3: named <c>&lt;set&gt;&lt;extension&gt;</c> (<c>.mamez</c>, <c>.fbneoz</c>, <c>.fbneo7z</c>) after the DAT's spelling, grouped under the parent's base title when
    /// <c>cloneof</c> names a set the DAT has; no regions, languages or revision; a BIOS set is flagged <c>BIOS</c>.
    /// </summary>
    internal static Release MameRelease(MameSet set, DatStore store, string extension)
    {
        var titled = set.CloneOf is { } parent && store.BySetName(parent) is { } parentSet ? parentSet : set;
        return new Release(set.Name + extension, NoIntroName.Parse(titled.Description).BaseTitle, set.Description, [], [], null,
            set.IsBios ? ["BIOS"] : [], IdentificationMethod.Dat, store.MetadataForSet(set.Name), null);
    }

    /// <summary>Arcade G2: the release header, where parent/clone and BIOS links are kept.</summary>
    internal static RomHeader MameHeader(MameSet set, MameMatch match) => new(null, null, true, new Dictionary<string, object?>
    {
        ["setName"] = set.Name,
        ["cloneOf"] = set.CloneOf,
        ["romOf"] = set.RomOf,
        ["isBios"] = set.IsBios,
        ["selfContained"] = match.SelfContained,
    });

    /// <summary>Arcade I5: the missing ROM names, cut after 20.</summary>
    internal static string MissingRomList(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(20)) + (names.Count > 20 ? $", … and {names.Count - 20} more" : "");
}
