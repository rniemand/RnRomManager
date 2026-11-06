using System.IO.Compression;
using System.Security.Cryptography;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>
/// What a write puts in the ZIP: the complete new <c>info.json</c>, plus content for the entries that are new or
/// replaced (full entry names, e.g. <c>roms/Tetris (World).gb</c>). Every other entry the new <c>info.json</c> lists
/// is copied from the existing ZIP; entries it no longer lists are dropped. The note of every listed ROM
/// (<see cref="LibraryZip.NoteEntry"/>) is copied too unless it's in <paramref name="NewEntries"/> or
/// <paramref name="RemovedEntries"/>; notes of ROMs no longer listed are dropped.
/// </summary>
public sealed record LibraryZipChanges(
    InfoJsonGame Info,
    IReadOnlyDictionary<string, Func<Stream>> NewEntries,
    IReadOnlySet<string>? RemovedEntries = null);

/// <summary>A written entry didn't match <c>info.json</c> (<c>VerifyFailed</c>, spec §6.1, §7.2 step 9).</summary>
public sealed class LibraryZipVerifyException(string entryName, string problem, Exception? inner = null)
    : Exception($"ZIP entry '{entryName}': {problem}", inner)
{
    public string EntryName { get; } = entryName;
}

/// <summary>
/// Atomic library ZIP writes (spec §6.1): stream into <c>&lt;name&gt;.zip.tmp</c> next to the target, re-open it and
/// verify <c>info.json</c> and every ROM's size + SHA1, then <c>File.Move(overwrite)</c> with 3 retries at 500 ms
/// (§3.1). Any failure deletes the tmp file and leaves the original untouched.
/// <c>existing</c> is closed before the move: Windows can't replace a file that's open, even with
/// <c>FileShare.Delete</c> (D44).
/// </summary>
public sealed class LibraryZipWriter(InfoJsonReader reader)
{
    public const int MoveRetries = 3;
    public static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <param name="target">Final ZIP path; its folder must exist.</param>
    /// <param name="existing">The game's current ZIP (may be at another path after a title edit), or null for a new game.
    /// Disposed by the writer once verified; disposing it again is harmless.</param>
    /// <exception cref="LibraryZipVerifyException">An entry (copied or new) failed verification.</exception>
    /// <exception cref="IOException">Write or move failed (<c>IoError</c>).</exception>
    public async Task WriteAsync(string target, LibraryZip? existing, LibraryZipChanges changes, CancellationToken ct = default)
    {
        if (existing is not null && existing.InfoJson?.Kind is not InfoJsonReadResult.Outcome.Ok)
            throw new InvalidOperationException($"Existing ZIP '{existing.Path}' has no valid current info.json; it can't be written from.");

        var entries = ListedEntries(changes.Info);
        var notes = changes.Info.Roms.Select(r => LibraryZip.NoteEntry(r.FileName)).ToList();
        if (changes.NewEntries.Keys.FirstOrDefault(k => !entries.ContainsKey(k) && !notes.Contains(k)) is { } stray)
            throw new ArgumentException($"New entry '{stray}' isn't listed in info.json.", nameof(changes));

        var tmp = target + ".tmp";
        try
        {
            await WriteTmpAsync(tmp, existing, changes, entries, notes, ct).ConfigureAwait(false);
            await VerifyAsync(tmp, changes.Info, ct).ConfigureAwait(false);
            existing?.Dispose();
            await MoveAsync(tmp, target, ct).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    /// <summary>Entry name → size from <c>info.json</c>, for every ROM and image.</summary>
    private static Dictionary<string, long> ListedEntries(InfoJsonGame info)
    {
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var rom in info.Roms)
            entries.Add(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
        foreach (var image in info.Images)
            entries.Add(LibraryZip.ImageEntry(image.FileName), image.SizeBytes);
        return entries;
    }

    private static async Task WriteTmpAsync(
        string tmp, LibraryZip? existing, LibraryZipChanges changes, Dictionary<string, long> entries, List<string> notes,
        CancellationToken ct)
    {
        await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        await using (var info = archive.CreateEntry(LibraryZip.InfoJsonEntry, CompressionLevel.Optimal).Open())
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
            await CopyEntryAsync(archive, name, source, ct).ConfigureAwait(false);
        }

        // Notes are optional: a missing one is simply not written (B8).
        foreach (var name in notes)
        {
            if (changes.NewEntries.TryGetValue(name, out var open))
                await CopyEntryAsync(archive, name, new LimitStream(open(), LibraryZip.MaxNoteBytes), ct).ConfigureAwait(false);
            else if (changes.RemovedEntries?.Contains(name) != true && existing is not null && existing.HasEntry(name))
                await CopyEntryAsync(archive, name, existing.OpenEntry(name, LibraryZip.MaxNoteBytes), ct).ConfigureAwait(false);
        }
    }

    private static async Task CopyEntryAsync(ZipArchive archive, string name, Stream source, CancellationToken ct)
    {
        await using (source)
        await using (var destination = archive.CreateEntry(name, CompressionLevel.Optimal).Open())
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

    private async Task VerifyAsync(string tmp, InfoJsonGame expected, CancellationToken ct)
    {
        using var zip = LibraryZip.Open(tmp, reader);
        if (zip.InfoJson?.Kind is not InfoJsonReadResult.Outcome.Ok)
            throw new LibraryZipVerifyException(LibraryZip.InfoJsonEntry, zip.InfoJson?.Rule ?? "missing");

        foreach (var rom in expected.Roms)
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
