using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>
/// W3: what a multi-release move is about to do, written beside its verified source backup before any byte moves, so
/// an Admin can tell the move's two ZIPs and its release apart even after both ZIPs were replaced (W7). It binds the
/// backup (by SHA256) to the games, their library-relative ZIP paths, the moved release's SHA1 and its pointer before
/// and after the move. Neither the record nor the backup alone authorizes recovery.
/// </summary>
/// <param name="Files">
/// B18 (v2): the release's set files in <c>files</c> order, each with its name before and after the move; null in v1,
/// written by a release without files.
/// </param>
public sealed record MoveRecord(
    int Version,
    string BackupSha256,
    Guid SourceGameId,
    string SourceZipPath,
    Guid TargetGameId,
    string TargetZipPath,
    string RomSha1,
    string SourceExternalFileName,
    string TargetExternalFileName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MoveRecordFile>? Files = null)
{
    /// <summary>v1: one file per release (no <see cref="Files"/>).</summary>
    public const int SingleFileVersion = 1;

    /// <summary>v2 (B18): a release with set files.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The version a move of a release with (or without) set files writes.</summary>
    public static int VersionFor(IReadOnlyList<MoveRecordFile>? files) => files is null ? SingleFileVersion : CurrentVersion;
}

/// <summary>B18: one set file of a moved release, before and after the move.</summary>
public sealed record MoveRecordFile(string SourceExternalFileName, string TargetExternalFileName);

/// <summary>Publishes and reads the <c>&lt;backup&gt;.json</c> companion of an <see cref="ExternalZipBackup"/> (spec W3, W7).</summary>
public static class ExternalMoveRecord
{
    /// <summary>B18: 99 pairs of 250-byte names fit even when every byte is written as a 6-byte <c>\uXXXX</c> escape.</summary>
    public const int MaxBytes = 320 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string PathOf(ZipBackup backup) => backup.Path + ExternalRomName.RecordSuffix;

    /// <summary>
    /// Writes the record with the backup's protocol: an exclusively created <c>.partial</c>, reopened and compared,
    /// published by a rename that never replaces anything, then read back and validated against the backup.
    /// </summary>
    /// <exception cref="IOException">The record wasn't published (<c>IoError</c>, before any byte moves); an occupant is left as it is.</exception>
    public static async Task<ExternalFileSnapshot> WriteAsync(string libraryRoot, ZipBackup backup, MoveRecord record, InfoJsonReader reader,
        ExternalZipBackupHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaxBytes)
            throw new IOException($"The move record is {bytes.Length} bytes, over the {MaxBytes}-byte limit");
        try
        {
            // The pair is only usable together: the record must describe this backup before anything is written.
            await ValidateAsync(libraryRoot, backup.Path, record, reader, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException($"The move record doesn't match {backup.Path}: {ex.Message}", ex);
        }
        var final = PathOf(backup);
        var partial = final + ExternalRomName.PartialSuffix;

        FileIdentity? owned = null;
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                owned = ExternalRomPath.SnapshotOf(output)?.Identity ?? throw new IOException($"{partial} was created but can't be identified");
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            hooks?.AfterCopy?.Invoke(partial);

            ExternalFileSnapshot verified;
            var reopened = ExternalRomPath.OpenRead(libraryRoot, partial, FileShare.Read);
            using (var stream = reopened.Stream)
            {
                if (reopened.Status != ExternalPathStatus.Found || reopened.Snapshot!.Identity != owned)
                    throw new IOException($"{partial} is no longer the file this record created");
                if (!(await ReadCappedAsync(stream!, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                    throw new IOException($"{partial} doesn't read back as the record that was written");
                verified = reopened.Snapshot;
            }
            hooks?.BeforePromotion?.Invoke(partial);

            if (ExternalRomPath.ClassifyFile(libraryRoot, partial).Snapshot != verified)
                throw new IOException($"{partial} is no longer the verified record");
            var renamed = NoReplaceRename.Move(partial, final);
            if (!renamed.Renamed)
                throw new IOException($"{final} can't be published: {renamed.Details ?? renamed.Status.ToString()}");
            owned = null; // published: nothing at the staging name is ours any more
            hooks?.AfterPromotion?.Invoke(final);
            if (ExternalRomPath.ClassifyFile(libraryRoot, final).Snapshot != verified)
                throw new IOException($"{final} is no longer the verified record");
            return verified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (owned is not null && ExternalRomPath.ClassifyFile(libraryRoot, partial).Snapshot?.Identity == owned)
                    File.Delete(partial);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The record's own failure is the one to report; a .partial is never read as a record.
            }
            throw ex as IOException ?? new IOException(ex.Message, ex);
        }
    }

    /// <summary>
    /// W7: reads the record beside <paramref name="backupPath"/> and checks it against that backup: a supported
    /// version, the backup's SHA256, distinct games with valid library paths and plain pointer names, and a backed-up
    /// ZIP of the source game holding exactly one release with the recorded SHA1, whose pointer is the recorded
    /// source pointer (ignoring case).
    /// </summary>
    /// <exception cref="InvalidDataException">The pair doesn't identify a move; recovery must stop.</exception>
    /// <exception cref="IOException">A member can't be read.</exception>
    public static async Task<MoveRecord> ReadAsync(string libraryRoot, string backupPath, InfoJsonReader reader, CancellationToken cancellationToken = default)
    {
        var opened = ExternalRomPath.OpenRead(libraryRoot, backupPath + ExternalRomName.RecordSuffix, FileShare.Read);
        if (opened.Status != ExternalPathStatus.Found)
            throw new IOException($"The move record of {backupPath} can't be read: {opened.Details}");
        byte[] bytes;
        using (var stream = opened.Stream!)
            bytes = await ReadCappedAsync(stream, cancellationToken).ConfigureAwait(false);

        MoveRecord record;
        try
        {
            record = JsonSerializer.Deserialize<MoveRecord>(bytes, Json) ?? throw new InvalidDataException("the move record is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the move record isn't valid JSON: {ex.Message}", ex);
        }
        await ValidateAsync(libraryRoot, backupPath, record, reader, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <exception cref="InvalidDataException">The record doesn't describe a move of this backup.</exception>
    private static async Task ValidateAsync(string libraryRoot, string backupPath, MoveRecord record, InfoJsonReader reader, CancellationToken cancellationToken)
    {
        if (record.Version is not (MoveRecord.SingleFileVersion or MoveRecord.CurrentVersion))
            throw new InvalidDataException($"move record version {record.Version} isn't supported");
        if (record.Version == MoveRecord.SingleFileVersion ? record.Files is not null : record.Files is not { Count: > 0 })
            throw new InvalidDataException($"a version {record.Version} move record {(record.Version == MoveRecord.SingleFileVersion ? "has no" : "lists its")} files");
        if (Problem(record) is { } invalid)
            throw new InvalidDataException(invalid);

        var backup = ExternalRomPath.OpenRead(libraryRoot, backupPath, FileShare.Read);
        if (backup.Status != ExternalPathStatus.Found)
            throw new IOException($"{backupPath} can't be read: {backup.Details}");
        using var copy = new MemoryStream();
        using (var stream = backup.Stream!)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                copy.Write(buffer, 0, read);
            }
            if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), record.BackupSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the move record names another backup (SHA256 differs)");
        }

        copy.Position = 0;
        using var zip = LibraryZip.Open(copy, backupPath, reader);
        if (zip.InfoJson is not { Kind: InfoJsonReadResult.Outcome.Ok, Game: { } game })
            throw new InvalidDataException("the backup's info.json can't be read");
        if (game.GameId != record.SourceGameId)
            throw new InvalidDataException($"the backup is game {game.GameId}, not the recorded source {record.SourceGameId}");
        var releases = game.Roms.Where(r => r.Metadata.Sha1 == record.RomSha1).ToList();
        if (releases.Count != 1)
            throw new InvalidDataException($"the backup holds {releases.Count} releases with sha1 {record.RomSha1}, not one");
        if (!string.Equals(releases[0].ExternalFileName, record.SourceExternalFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"the backed-up release's pointer is {releases[0].ExternalFileName}, not the recorded {record.SourceExternalFileName}");
        // B18: a v2 record's files are the backed-up release's files, in order.
        if (record.Files is { } files && (releases[0].Files is not { } backedUp || backedUp.Count != files.Count
            || backedUp.Zip(files).Any(p => !string.Equals(p.First.ExternalFileName, p.Second.SourceExternalFileName, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("the backed-up release's files aren't the recorded ones");
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            total += read;
        if (total > MaxBytes)
            throw new InvalidDataException($"the move record is over {MaxBytes} bytes");
        return buffer[..total];
    }

    /// <returns>What is wrong with the record's shape (G4 paths, E3 pointers); null when nothing is.</returns>
    private static string? Problem(MoveRecord record)
    {
        if (record.SourceGameId == record.TargetGameId)
            return "the source and target games must differ";
        if (record.RomSha1.Length != 40 || !record.RomSha1.All(char.IsAsciiHexDigit))
            return "romSha1 must be 40 hex digits";
        if (record.BackupSha256.Length != 64 || !record.BackupSha256.All(char.IsAsciiHexDigit))
            return "backupSha256 must be 64 hex digits";
        foreach (var path in new[] { record.SourceZipPath, record.TargetZipPath })
        {
            var parts = path.Split('/');
            if (path.Length == 0 || path.Contains('\\') || Path.IsPathRooted(path) || parts.Any(p => p is "" or "." or "..")
                || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return $"'{path}' isn't a library-relative ZIP path";
        }
        foreach (var pointer in new[] { record.SourceExternalFileName, record.TargetExternalFileName }
            .Concat((record.Files ?? []).SelectMany(f => new[] { f.SourceExternalFileName, f.TargetExternalFileName })))
        {
            if (pointer is "." or ".." || InfoJsonValidator.PortableNameProblem(pointer) is not null
                || Encoding.UTF8.GetByteCount(pointer) > InfoJsonValidator.MaxExternalFileNameBytes)
                return $"'{pointer}' isn't a plain, portable file name";
        }
        return null;
    }
}
