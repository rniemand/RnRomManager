using System.IO.Compression;
using System.Security.Cryptography;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>
/// Atomic library ZIP writes (spec §6.1): stream into <c>&lt;name&gt;.zip.tmp</c> next to the target, re-open it and
/// verify <c>info.json</c> and every ROM's size + SHA1, then <c>File.Move(overwrite)</c> with 3 retries at 500 ms
/// (§3.1). Any failure deletes the tmp file and leaves the original untouched.
/// <c>existing</c> is closed before the move: Windows can't replace a file that's open, even with
/// <c>FileShare.Delete</c> (D44).
/// </summary>
/// <param name="compression">Z2: the level of every entry this writer writes (the game's system level).</param>
public sealed class LibraryZipWriter(InfoJsonReader reader, ZipCompression compression = ZipCompression.Med)
{
    public const int MoveRetries = 3;
    public static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The default <see cref="FastCompressionAboveBytes"/>: 128 MiB, above every non-DS embedded system's maximum.</summary>
    public const long DefaultFastCompressionAboveBytes = 128L * 1024 * 1024;

    /// <summary>
    /// A <c>roms/</c> entry larger than this is written with <see cref="CompressionLevel.Fastest"/>, so a ZIP rewrite of a
    /// game with large releases doesn't spend minutes re-deflating them. Lowered by tests.
    /// </summary>
    public long FastCompressionAboveBytes { get; init; } = DefaultFastCompressionAboveBytes;

    /// <summary>
    /// Z2: <see cref="ZipCompression.Store"/> → <see cref="CompressionLevel.NoCompression"/> and
    /// <see cref="ZipCompression.High"/> → <see cref="CompressionLevel.SmallestSize"/> for every entry;
    /// <see cref="ZipCompression.Med"/> → <see cref="CompressionLevel.Fastest"/> for a <c>roms/</c> or <c>saves/</c>
    /// entry larger than <paramref name="fastAbove"/>, <see cref="CompressionLevel.Optimal"/> for everything else
    /// (<c>info.json</c>, images, notes, smaller ROMs).
    /// </summary>
    public static CompressionLevel CompressionFor(
        string entryName,
        long sizeBytes,
        long fastAbove,
        ZipCompression compression) => compression switch
    {
        ZipCompression.Store => CompressionLevel.NoCompression,
        ZipCompression.High => CompressionLevel.SmallestSize,
        _ => (entryName.StartsWith(LibraryZip.RomsFolder, StringComparison.Ordinal) || entryName.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal))
             && sizeBytes > fastAbove
            ? CompressionLevel.Fastest
            : CompressionLevel.Optimal,
    };

    /// <summary>The plain write, for a game whose ROMs are all in its ZIP.</summary>
    /// <param name="target">Final ZIP path; its folder must exist.</param>
    /// <param name="existing">The game's current ZIP (may be at another path after a title edit), or null for a new game.
    /// Disposed by the writer once verified; disposing it again is harmless.</param>
    /// <exception cref="LibraryZipVerifyException">An entry (copied or new) failed verification.</exception>
    /// <exception cref="IOException">Write or move failed (<c>IoError</c>).</exception>
    public async Task WriteAsync(
        string target,
        LibraryZip? existing,
        LibraryZipChanges changes,
        CancellationToken ct = default)
    {
        var (entries, notes, saves) = Plan(existing, changes);
        var tmp = target + ".tmp";
        try
        {
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await WriteTmpAsync(file, existing, changes, entries, notes, saves, FastCompressionAboveBytes, compression, ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true); // on disk before it replaces the old ZIP and callers delete the source
            }
            using (var zip = LibraryZip.Open(tmp, reader))
                await VerifyAsync(zip, changes.Info, saves, ct).ConfigureAwait(false);
            existing?.Dispose();
            await MoveAsync(tmp, target, ct).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    /// <summary>
    /// The write for an external game, and for any ZIP whose path must be checked (spec W1). On top of
    /// <see cref="WriteAsync"/>:
    /// <list type="bullet">
    /// <item>The whole path below <paramref name="libraryRoot"/> is checked with <see cref="ExternalRomPath"/>
    /// before the temp file is created, before verification and before publication: a link anywhere on it is an
    /// <c>IoError</c> and nothing is written.</item>
    /// <item>The temp file is created exclusively. An occupied temp name is an <c>IoError</c> and the occupant is
    /// left alone; the temp is verified, published and cleaned up only while it is still the file this write
    /// created.</item>
    /// <item>A new path (<paramref name="existing"/> null or at another path) is published without overwrite: if
    /// anything took the name meanwhile, in any letter case, it is an <c>IoError</c> and the occupant stays.</item>
    /// <item>A rebuild at the current path replaces the ZIP only while it is still the file
    /// <paramref name="existing"/> was opened from, on every attempt.</item>
    /// <item>Every file in <see cref="LibraryZipChanges.ExternalFiles"/> must still be the promoted file, with the
    /// length <c>info.json</c> records (no rehash: its bytes were hashed at ingest), before each attempt.</item>
    /// <item>A rebuild whose size and whole-second mtime would equal the old ZIP's gets a later mtime, so a sync
    /// after a failed mirror still sees the change.</item>
    /// </list>
    /// </summary>
    /// <param name="libraryRoot"><c>Paths:Roms</c>.</param>
    /// <returns>
    /// The published ZIP, as verified before its rename. Callers stamp the mirror from it and check it before a
    /// later undo or cleanup, never from a fresh look at the path.
    /// </returns>
    /// <exception cref="LibraryZipVerifyException">An entry or a touched external file failed verification (<c>VerifyFailed</c>).</exception>
    /// <exception cref="IOException">An unsafe or occupied path, or a failed write or rename (<c>IoError</c>).</exception>
    public async Task<ExternalFileSnapshot> WriteCheckedAsync(
        string libraryRoot,
        string target,
        LibraryZip? existing,
        LibraryZipChanges changes,
        CancellationToken ct = default,
        LibraryZipWriterHooks? hooks = null)
    {
        var (entries, notes, saves) = Plan(existing, changes);
        target = Path.GetFullPath(target);
        var folder = Path.GetDirectoryName(target)!;
        var rebuild = existing is not null && string.Equals(Path.GetFullPath(existing.Path), target, StringComparison.OrdinalIgnoreCase);
        if (rebuild && existing!.Snapshot is null)
            throw new IOException($"'{target}' was opened without a file snapshot; it can't be replaced safely.");
        var externalFiles = ExternalFiles(changes);

        var directory = ExternalRomPath.ClassifyDirectory(libraryRoot, folder);
        if (directory.Status != ExternalPathStatus.Found)
            throw new IOException($"The folder of '{target}' can't be written to: {directory.Details}");
        CheckDestination(libraryRoot, target, rebuild ? existing!.Snapshot : null);

        var tmp = target + ".tmp";
        FileIdentity? tmpIdentity = null;
        try
        {
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                tmpIdentity = ExternalRomPath.SnapshotOf(file)?.Identity
                    ?? throw new IOException($"'{tmp}' was created but can't be identified.");
                await WriteTmpAsync(file, existing, changes, entries, notes, saves, FastCompressionAboveBytes, compression, ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            hooks?.AfterTempClosed?.Invoke();

            // Verify through a checked handle on the very file this write created.
            var opened = ExternalRomPath.OpenRead(libraryRoot, tmp, FileShare.Read | FileShare.Delete);
            if (opened.Status != ExternalPathStatus.Found || opened.Snapshot!.Identity != tmpIdentity)
            {
                opened.Stream?.Dispose();
                throw new IOException($"'{tmp}' is no longer the file this write created: {opened.Details ?? "it was replaced"}");
            }
            var staged = opened.Snapshot;
            using (var zip = LibraryZip.Open(new BufferedStream(opened.Stream!), tmp, reader))
                await VerifyAsync(zip, changes.Info, saves, ct).ConfigureAwait(false);
            VerifyExternalFiles(libraryRoot, folder, externalFiles);

            if (rebuild)
                staged = Outdate(libraryRoot, tmp, staged, existing!.Snapshot!);
            existing?.Dispose();
            await PublishAsync(libraryRoot, tmp, target, folder, staged, rebuild ? existing!.Snapshot : null, externalFiles, hooks, ct).ConfigureAwait(false);
            return staged;
        }
        catch
        {
            // Only the temp this write created is cleaned up, never a file that took its name.
            if (tmpIdentity is not null && ExternalRomPath.ClassifyFile(libraryRoot, tmp) is { Status: ExternalPathStatus.Found } current
                && current.Snapshot!.Identity == tmpIdentity)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The original failure is the one to report; a leftover temp is found by integrity.
                }
            }
            throw;
        }
    }

    private static (Dictionary<string, long> Entries, List<string> Notes, List<SaveEntryPlan> Saves) Plan(LibraryZip? existing, LibraryZipChanges changes)
    {
        if (existing is not null && existing.InfoJson?.Kind is not InfoJsonReadResultOutcome.Ok)
            throw new InvalidOperationException($"Existing ZIP '{existing.Path}' has no valid current info.json; it can't be written from.");

        // B12: the reader refuses a bigger info.json, which would leave the game unreadable.
        var infoBytes = InfoJsonSerializer.Serialize(changes.Info).Length;
        if (infoBytes > InfoJsonReader.MaxBytes)
            throw new LibraryZipVerifyException(LibraryZip.InfoJsonEntry, $"info.json would be {infoBytes} bytes, over the {InfoJsonReader.MaxBytes}-byte limit");
        var entries = ListedEntries(changes.Info);
        var notes = changes.Info.Roms.Select(r => LibraryZip.NoteEntry(r.FileName)).ToList();
        if (changes.NewEntries.Keys.FirstOrDefault(k => !entries.ContainsKey(k) && !notes.Contains(k)) is { } stray)
            throw new ArgumentException($"New entry '{stray}' isn't listed in info.json.", nameof(changes));
        return (entries, notes, PlanSaves(existing, changes.Saves));
    }

    /// <summary>B3: the <c>saves/</c> entries of the new ZIP.</summary>
    private static List<SaveEntryPlan> PlanSaves(LibraryZip? existing, SavesChange? change)
    {
        var plan = new List<SaveEntryPlan>();
        if (change is null)
        {
            if (existing is null)
                return plan;
            var current = GameSaves.Read(existing);
            if (current.State == SavesState.Valid)
            {
                plan.Add(new(LibraryZip.SavesIndexEntry, existing.EntryLength(LibraryZip.SavesIndexEntry)!.Value));
                plan.AddRange(current.Index!.Saves.Select(s => new SaveEntryPlan(s.Entry, s.SizeBytes)));
            }
            else if (current.State == SavesState.Unreadable)
            {
                // No data loss on a write that wasn't about saves: every entry byte for byte, bounded by its own length.
                plan.AddRange(existing.EntryNames.Where(e => e.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal))
                    .Select(e => new SaveEntryPlan(e, existing.EntryLength(e)!.Value)));
            }
            return plan;
        }

        if (change.Index is null)
        {
            if (change.Added.Count > 0)
                throw new ArgumentException($"Save entry '{change.Added.Keys.First()}' isn't listed: the change removes saves.json.", nameof(change));
            return plan;
        }
        var saves = GameSaves.Records(change.Index);
        var listed = saves.Select(s => s.Entry).ToHashSet(StringComparer.Ordinal);
        if (change.Added.Keys.FirstOrDefault(k => !listed.Contains(k)) is { } stray)
            throw new ArgumentException($"Save entry '{stray}' isn't listed in saves.json.", nameof(change));
        plan.Add(new(LibraryZip.SavesIndexEntry, change.Index.Length, () => new MemoryStream(change.Index)));
        foreach (var save in saves)
        {
            if (change.Added.TryGetValue(save.Entry, out var open))
                plan.Add(new(save.Entry, save.SizeBytes, open, save.Sha1));
            else if (!change.Removed.Contains(save.Entry) && existing is not null && existing.HasEntry(save.Entry))
                plan.Add(new(save.Entry, save.SizeBytes));
            else
                throw new ArgumentException($"No content for save entry '{save.Entry}': not added and not in the existing ZIP.", nameof(change));
        }
        return plan;
    }

    /// <summary>Entry name → size from <c>info.json</c>, for every ROM stored in the ZIP and every image.</summary>
    private static Dictionary<string, long> ListedEntries(InfoJsonGame info)
    {
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var rom in info.Roms.Where(r => r.ExternalFileName is null))
            entries.Add(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
        foreach (var image in info.Images)
            entries.Add(LibraryZip.ImageEntry(image.FileName), image.SizeBytes);
        return entries;
    }

    /// <summary>The touched external files with the size <c>info.json</c> records for each.</summary>
    private static List<(string Name, ExternalFileSnapshot Snapshot, long SizeBytes)> ExternalFiles(LibraryZipChanges changes)
    {
        var files = new List<(string, ExternalFileSnapshot, long)>();
        foreach (var (name, snapshot) in changes.ExternalFiles ?? new Dictionary<string, ExternalFileSnapshot>())
        {
            // A main file or, on a FileSets system, a set file (B12).
            var size = changes.Info.Roms.FirstOrDefault(r => string.Equals(r.ExternalFileName, name, StringComparison.OrdinalIgnoreCase))?.Metadata.SizeBytes
                ?? changes.Info.Roms.SelectMany(r => r.Files ?? []).FirstOrDefault(f => string.Equals(f.ExternalFileName, name, StringComparison.OrdinalIgnoreCase))?.SizeBytes
                ?? throw new ArgumentException($"External file '{name}' isn't listed in info.json.", nameof(changes));
            files.Add((name, snapshot, size));
        }
        return files;
    }

    private static void VerifyExternalFiles(string libraryRoot, string folder, List<(string Name, ExternalFileSnapshot Snapshot, long SizeBytes)> files)
    {
        foreach (var (name, snapshot, sizeBytes) in files)
        {
            var check = ExternalRomPath.ClassifyFile(libraryRoot, Path.Combine(folder, name));
            if (check.Status != ExternalPathStatus.Found)
                throw new LibraryZipVerifyException(name, $"the external file is {check.Status.ToString().ToLowerInvariant()}: {check.Details}");
            if (check.Snapshot != snapshot)
                throw new LibraryZipVerifyException(name, "the external file is not the file this write placed");
            if (snapshot.Length != sizeBytes)
                throw new LibraryZipVerifyException(name, $"size {snapshot.Length} != info.json {sizeBytes}");
        }
    }

    /// <param name="replacing">The ZIP a rebuild replaces; null when publishing a new path, which must be free.</param>
    private static void CheckDestination(string libraryRoot, string target, ExternalFileSnapshot? replacing)
    {
        var check = ExternalRomPath.ClassifyFile(libraryRoot, target);
        if (replacing is null)
        {
            if (check.Status != ExternalPathStatus.Missing)
                throw new IOException($"'{target}' can't be published: {(check.Status == ExternalPathStatus.Found ? "the name is taken" : check.Details)}");
        }
        else if (check.Status != ExternalPathStatus.Found || check.Snapshot != replacing)
        {
            throw new IOException($"'{target}' is no longer the ZIP that was opened: {check.Details ?? "it was replaced"}");
        }
    }

    /// <summary>
    /// Gives the staged ZIP a whole-second mtime after the old one's when a sync couldn't tell them apart otherwise
    /// (same size, mtime not later).
    /// </summary>
    private static ExternalFileSnapshot Outdate(
        string libraryRoot,
        string tmp,
        ExternalFileSnapshot staged,
        ExternalFileSnapshot old)
    {
        var oldSecond = MirrorEntry.WholeSeconds(old.LastWriteTimeUtc);
        if (staged.Length != old.Length || MirrorEntry.WholeSeconds(staged.LastWriteTimeUtc) > oldSecond)
            return staged;

        File.SetLastWriteTimeUtc(tmp, oldSecond.AddSeconds(1));
        var check = ExternalRomPath.ClassifyFile(libraryRoot, tmp);
        if (check.Status != ExternalPathStatus.Found || check.Snapshot!.Identity != staged.Identity)
            throw new IOException($"'{tmp}' is no longer the file this write created: {check.Details ?? "it was replaced"}");
        if (MirrorEntry.WholeSeconds(check.Snapshot.LastWriteTimeUtc) <= oldSecond)
            throw new IOException($"'{tmp}' can't be given a modification time after {oldSecond:O}; the rewrite would be invisible to the next sync.");
        return check.Snapshot;
    }

    private static async Task PublishAsync(
        string libraryRoot,
        string tmp,
        string target,
        string folder,
        ExternalFileSnapshot staged,
        ExternalFileSnapshot? replacing,
        List<(string Name, ExternalFileSnapshot Snapshot, long SizeBytes)> externalFiles,
        LibraryZipWriterHooks? hooks,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            hooks?.BeforePublicationAttempt?.Invoke(attempt);

            // Everything is checked again on every attempt: a retry must not act on what an earlier attempt saw.
            var current = ExternalRomPath.ClassifyFile(libraryRoot, tmp);
            if (current.Status != ExternalPathStatus.Found || current.Snapshot != staged)
                throw new IOException($"'{tmp}' is no longer the verified file: {current.Details ?? "it was replaced or changed"}");
            VerifyExternalFiles(libraryRoot, folder, externalFiles);
            CheckDestination(libraryRoot, target, replacing);

            string failure;
            var retry = true; // a sharing violation may clear; a taken name or an unsupported rename won't
            try
            {
                if (hooks?.FailPublicationAttempt?.Invoke(attempt) == true)
                    throw new IOException("simulated sharing violation");
                if (replacing is not null)
                {
                    File.Move(tmp, target, overwrite: true);
                    return;
                }

                var renamed = NoReplaceRename.PublishStaged(tmp, target, staged, hooks?.Rename);
                if (renamed.Renamed)
                    return;
                failure = renamed.Details!;
                retry = renamed.Status == NoReplaceRenameStatus.Failed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex.Message;
            }

            if (!retry || attempt >= MoveRetries)
                throw new IOException($"'{target}' could not be published: {failure}");
            await Task.Delay(MoveRetryDelay, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteTmpAsync(
        FileStream file,
        LibraryZip? existing,
        LibraryZipChanges changes,
        Dictionary<string, long> entries,
        List<string> notes,
        List<SaveEntryPlan> saves,
        long fastAbove,
        ZipCompression compression,
        CancellationToken ct)
    {
        await using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);

        await using (var info = archive.CreateEntry(LibraryZip.InfoJsonEntry, CompressionFor(LibraryZip.InfoJsonEntry, 0, fastAbove, compression)).Open())
            await info.WriteAsync(InfoJsonSerializer.Serialize(changes.Info), ct).ConfigureAwait(false);

        foreach (var (name, size) in entries)
        {
            Stream source;
            if (changes.NewEntries.TryGetValue(name, out var open))
                source = new LimitStream(open(), size);
            else if (existing is not null && existing.HasEntry(name))
                source = existing.OpenEntry(name, size);
            else
                throw new ArgumentException($"No content for entry '{name}': not new and not in the existing ZIP.", nameof(changes));
            await CopyEntryAsync(archive, name, source, CompressionFor(name, size, fastAbove, compression), ct).ConfigureAwait(false);
        }

        // Notes are optional: a missing one is simply not written (B8).
        foreach (var name in notes)
        {
            if (changes.NewEntries.TryGetValue(name, out var open))
                await CopyEntryAsync(archive, name, new LimitStream(open(), LibraryZip.MaxNoteBytes),
                    CompressionFor(name, LibraryZip.MaxNoteBytes, fastAbove, compression), ct).ConfigureAwait(false);
            else if (changes.RemovedEntries?.Contains(name) != true && existing is not null && existing.HasEntry(name))
                await CopyEntryAsync(archive, name, existing.OpenEntry(name, LibraryZip.MaxNoteBytes),
                    CompressionFor(name, LibraryZip.MaxNoteBytes, fastAbove, compression), ct).ConfigureAwait(false);
        }

        foreach (var save in saves)
        {
            var source = save.New is { } open ? new LimitStream(open(), save.Size) : existing!.OpenEntry(save.Name, save.Size);
            await CopyEntryAsync(archive, save.Name, source, CompressionFor(save.Name, save.Size, fastAbove, compression), ct).ConfigureAwait(false);
        }
    }

    private static async Task CopyEntryAsync(
        ZipArchive archive,
        string name,
        Stream source,
        CompressionLevel level,
        CancellationToken ct)
    {
        await using (source)
        await using (var destination = archive.CreateEntry(name, level).Open())
        {
            try
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LimitExceededException or InvalidDataException)
            {
                // A corrupt or oversized entry blocks the write and names the entry (D34).
                throw new LibraryZipVerifyException(name, ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// <c>info.json</c> reads back valid, and every ROM stored in the ZIP has its recorded size and SHA1. B3: every
    /// planned <c>saves/</c> entry exists with its size; only newly written ones are hashed.
    /// </summary>
    private static async Task VerifyAsync(
        LibraryZip zip,
        InfoJsonGame expected,
        List<SaveEntryPlan> saves,
        CancellationToken ct)
    {
        if (zip.InfoJson?.Kind is not InfoJsonReadResultOutcome.Ok)
            throw new LibraryZipVerifyException(LibraryZip.InfoJsonEntry, zip.InfoJson?.Rule ?? "missing");

        foreach (var rom in expected.Roms.Where(r => r.ExternalFileName is null))
        {
            var name = LibraryZip.RomEntry(rom.FileName);
            byte[] sha1;
            long length;
            try
            {
                await using var stream = zip.OpenEntry(name, rom.Metadata.SizeBytes);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = new byte[81920];
                length = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
                sha1 = hash.GetHashAndReset();
            }
            catch (Exception ex) when (ex is LimitExceededException or InvalidDataException or FileNotFoundException)
            {
                throw new LibraryZipVerifyException(name, ex.Message, ex);
            }

            if (length != rom.Metadata.SizeBytes)
                throw new LibraryZipVerifyException(name, $"size {length} != info.json {rom.Metadata.SizeBytes}");
            if (Convert.ToHexStringLower(sha1) != rom.Metadata.Sha1)
                throw new LibraryZipVerifyException(name, $"SHA1 {Convert.ToHexStringLower(sha1)} != info.json {rom.Metadata.Sha1}");
        }

        foreach (var save in saves)
        {
            if (zip.EntryLength(save.Name) != save.Size)
                throw new LibraryZipVerifyException(save.Name, $"missing or not {save.Size} bytes");
            if (save.Sha1 is null)
                continue;
            string actual;
            try
            {
                await using var stream = zip.OpenEntry(save.Name, save.Size);
                actual = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is LimitExceededException or InvalidDataException)
            {
                throw new LibraryZipVerifyException(save.Name, ex.Message, ex);
            }
            if (actual != save.Sha1)
                throw new LibraryZipVerifyException(save.Name, $"SHA1 {actual} != saves.json {save.Sha1}");
        }
    }

    private static async Task MoveAsync(string tmp, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveRetries)
            {
                await Task.Delay(MoveRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
