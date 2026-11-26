using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Integrity.IntegrityIssueType;

namespace RnRomManager.Core.Integrity;
/// <summary>The checks of one library ZIP for <see cref="IntegrityJob"/>: its <c>info.json</c>, entries, location and actions (A5).</summary>
internal sealed class ZipIntegrityCheck(
    bool hashes,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    InfoJsonReader reader,
    ILibraryMirror mirror)
{
    /// <summary>A5: <c>cover_image_pulled_at</c> per game, read for runs that open ZIPs with hashing (Deep, Game).</summary>
    internal IReadOnlyDictionary<Guid, DateTime>? Pulled { get; set; }

    /// <summary>A5: the action mismatches this run repaired.</summary>
    internal List<IntegrityFinding> ActionRepairs { get; } = [];

    /// <summary>
    /// A5: when the mirrored cover-pulled time differs from the ZIP's (both whole UTC seconds), the game is mirrored again
    /// and the repair recorded as <c>DbRowUpdated</c>.
    /// </summary>
    private async Task CheckActionsAsync(string zipPath, InfoJsonGame game, GameRow? row)
    {
        if (Pulled is null || row is null || row.Id != game.GameId)
            return;
        DateTime? inZip = InfoJsonActions.Parse(game.Actions?.CoverImagePulled) is { } pulled ? MirrorEntry.WholeSeconds(pulled.UtcDateTime) : null;
        DateTime? inDb = Pulled.TryGetValue(game.GameId, out var stored) ? MirrorEntry.WholeSeconds(stored) : null;
        if (inZip == inDb)
            return;
        var file = new FileInfo(Path.Combine(paths.Value.Roms, zipPath));
        await mirror.ReplaceAsync(new MirrorEntry(game, zipPath, file.Length, file.LastWriteTimeUtc), CancellationToken.None).ConfigureAwait(false);
        ActionRepairs.Add(new IntegrityFinding(DbRowUpdated, zipPath, SystemId: game.System, GameId: game.GameId));
    }

    internal async Task CheckZipAsync(
        string zipPath,
        GameRow? row,
        Action<IntegrityFinding> add,
        CancellationToken ct)
    {
        var system = IntegrityJob.SystemOf(systems, zipPath);
        LibraryZip zip;
        try
        {
            zip = IntegrityJob.OpenChecked(paths.Value.Roms, zipPath, reader);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            add(new IntegrityFinding(ZipUnreadable, zipPath, SystemId: system, GameId: row?.Id, Details: ex.Message));
            if (row is not null)
                CheckLocation(zipPath, row.SystemId, row.Title, row.Id, add);
            return;
        }

        using (zip)
        {
            var problem = zip.InfoJson switch
            {
                null => new IntegrityFinding(InfoJsonMissing, zipPath, SystemId: system, GameId: row?.Id),
                { Kind: InfoJsonReadResultOutcome.Ok } => null,
                { Kind: InfoJsonReadResultOutcome.NewerSchema } r =>
                    new IntegrityFinding(InfoJsonNewerSchema, zipPath, SystemId: system, GameId: row?.Id, Details: $"schemaVersion {r.FileVersion}"),
                var r => new IntegrityFinding(InfoJsonInvalid, zipPath, SystemId: system, GameId: row?.Id, Details: r.Rule),
            };
            if (problem is not null)
            {
                add(problem);
                if (row is not null)
                    CheckLocation(zipPath, row.SystemId, row.Title, row.Id, add);
                return;
            }

            var game = zip.InfoJson!.Game!;
            CheckLocation(zipPath, game.System, game.Title, game.GameId, add);
            await CheckActionsAsync(zipPath, game, row).ConfigureAwait(false);
            IntegrityFinding Finding(string type, string? entry = null, string? details = null) =>
                new(type, zipPath, entry, game.System, game.GameId, details);
            // S7: its roms/ entries are checked below as for any embedded game.
            if (systems.ById(game.System) is { } handler && RomStorage.IsPending(handler, game))
                add(Finding(LegacyEmbeddedStorage));

            // G1: an external ROM has no roms/ entry; its file is checked by StatExternal.
            var listed = game.Roms.Where(r => r.ExternalFileName is null).Select(r => (Entry: LibraryZip.RomEntry(r.FileName), r.Metadata.SizeBytes, r.Metadata.Sha1))
                .Concat(game.Images.Select(i => (Entry: LibraryZip.ImageEntry(i.FileName), i.SizeBytes, i.Sha1)))
                .ToList();
            foreach (var (entry, size, sha1) in listed)
            {
                ct.ThrowIfCancellationRequested();
                if (zip.EntryLength(entry) is not { } length)
                {
                    add(Finding(EntryMissing, entry));
                    continue;
                }
                if (length != size)
                    add(Finding(EntrySizeMismatch, entry, $"entry {length} bytes, info.json {size}"));
                if (hashes && await HashAsync(zip, entry, length, ct).ConfigureAwait(false) is var (actual, error) && actual != sha1)
                    add(Finding(HashMismatch, entry, error ?? $"SHA1 {actual}, info.json {sha1}"));
            }

            var names = listed.Select(l => l.Entry).ToHashSet(StringComparer.Ordinal);
            // Notes of listed ROMs are expected (B10); orphan notes are still reported.
            names.UnionWith(game.Roms.Select(r => LibraryZip.NoteEntry(r.FileName)));
            // B6: a valid saves.json and the entries it lists; with none, every saves/ entry is unlisted.
            if (GameSaves.Read(zip) is { State: SavesState.Valid, Index: { } saves })
                names.UnionWith([LibraryZip.SavesIndexEntry, .. saves.Saves.Select(s => s.Entry)]);
            foreach (var entry in zip.EntryNames.Where(e => e != LibraryZip.InfoJsonEntry && !names.Contains(e)))
                add(Finding(EntryUnlisted, entry));

            // v3: the codes are about the cover (I8), whatever its type.
            var cover = game.Images.FirstOrDefault(i => i.IsCover);
            if (cover is null)
                add(Finding(MissingBoxArt));
            else if (cover.IsDefault)
                add(Finding(DefaultBoxArt));
        }
    }

    /// <returns>The entry's SHA1, or null plus why it couldn't be read.</returns>
    private static async Task<(string? Sha1, string? Error)> HashAsync(LibraryZip zip, string entry, long length, CancellationToken ct)
    {
        try
        {
            await using var stream = zip.OpenEntry(entry, length);
            return (Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false)), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or LimitExceededException)
        {
            return (null, $"unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// §12.2 <c>WrongLocation</c>: system/bucket/name ≠ §6.1 (case-insensitive, §5); a clash suffix ` (N)` is accepted.
    /// An external game's name is the budgeted one its builder gives the title (E2), suffixed or not; a
    /// <c>LegacyEmbeddedRoms</c> system accepts either name in either setting (S7a, B5).
    /// </summary>
    internal void CheckLocation(
        string zipPath,
        string systemId,
        string title,
        Guid gameId,
        Action<IntegrityFinding> add)
    {
        var parts = zipPath.Split('/');
        var name = parts[^1];
        var handler = systems.ById(systemId);
        var external = handler is { EmbedRomsInZip: false };
        var stem = external ? Path.GetFileNameWithoutExtension(ExternalRomName.ZipName(title)) : FileNameSanitizer.Sanitize(title);
        var embeddedStem = FileNameSanitizer.Sanitize(title);
        var embeddedOk = name.Equals(embeddedStem + ".zip", StringComparison.OrdinalIgnoreCase) || IsClashSuffixed(name, embeddedStem);
        // S7a, B5: the storage job keeps each ZIP's path, so a legacy system's ZIP may carry either name rule, whatever the setting.
        var externalOk = ExternalRomName.IsZipNameFor(title, name);
        var nameOk = handler is { LegacyEmbeddedRoms: true } ? externalOk || embeddedOk : external ? externalOk : embeddedOk;
        var ok = parts.Length == 3 && nameOk
            && parts[0].Equals(systemId, StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals(Bucket.For(name), StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            var expected = $"{systemId}/{Bucket.For(stem + ".zip")}/{stem}.zip";
            add(new IntegrityFinding(WrongLocation, zipPath, SystemId: systemId, GameId: gameId, Details: $"expected {expected}"));
        }
    }

    internal static bool IsClashSuffixed(string name, string stem)
    {
        var prefix = stem + " (";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(").zip", StringComparison.OrdinalIgnoreCase))
            return false;
        var number = name[prefix.Length..^").zip".Length];
        return int.TryParse(number, System.Globalization.NumberStyles.None, null, out var n) && n >= 2 && number[0] != '0';
    }
}
