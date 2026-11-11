using System.Security.Cryptography;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>A verified backup of a source ZIP (spec W3): where it is, the file it is, and its SHA256.</summary>
public sealed record ZipBackup(string Path, ExternalFileSnapshot Snapshot, string Sha256);

/// <summary>Test seams of <see cref="ExternalZipBackup.CreateAsync"/>: points where a test swaps a file mid-backup.</summary>
public sealed class ExternalZipBackupHooks
{
    /// <summary>Runs once the copy is flushed, before it is reopened to be verified.</summary>
    public Action<string>? AfterCopy { get; init; }

    /// <summary>Runs with the staging path once the copy is verified, right before the checks that precede promotion.</summary>
    public Action<string>? BeforePromotion { get; init; }

    /// <summary>Runs with the backup path right after the rename that published it, before it is checked there.</summary>
    public Action<string>? AfterPromotion { get; init; }
}

/// <summary>
/// W3: before a move touches a multi-release external game, its source ZIP is copied to an owned, verified backup
/// beside it, so the original metadata and notes survive whatever happens to either ZIP. The copy is streamed from a
/// checked handle that must open the ZIP the move opened, into an exclusively created <c>.partial</c> file; it is
/// verified by reopening that same file (length and SHA256), then published under its <c>.move-backup.tmp</c> name
/// with a rename that never replaces anything. An interrupted copy is only ever a <c>.partial</c>, never mistaken for
/// a backup. Nothing that isn't the file this backup created is promoted or removed.
/// </summary>
public static class ExternalZipBackup
{
    /// <param name="libraryRoot"><c>Paths:Roms</c>.</param>
    /// <param name="zipPath">The source ZIP's full path.</param>
    /// <param name="source">The source ZIP as the move opened it; the backup must be a copy of exactly that file.</param>
    /// <exception cref="IOException">The backup couldn't be made (<c>IoError</c>); the source is untouched, and any occupant is left as it is.</exception>
    public static async Task<ZipBackup> CreateAsync(string libraryRoot, string zipPath, ExternalFileSnapshot source,
        ExternalZipBackupHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        var folder = ExternalRomPath.ClassifyDirectory(libraryRoot, Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        if (folder.Status != ExternalPathStatus.Found)
            throw new IOException($"The folder of {zipPath} can't hold a backup: {folder.Details}");
        var reservations = new ExternalNameReservations(Directory.EnumerateFileSystemEntries(folder.Path).Select(e => Path.GetFileName(e)!));
        var backup = Path.Combine(folder.Path, ExternalRomName.AllocateMoveBackup(Path.GetFileName(zipPath), reservations));
        var partial = backup + ExternalRomName.PartialSuffix;

        var opened = ExternalRomPath.OpenRead(libraryRoot, zipPath, FileShare.Read | FileShare.Delete, source);
        if (opened.Status != ExternalPathStatus.Found)
            throw new IOException($"{zipPath} can't be backed up: {opened.Details ?? opened.Status.ToString()}");

        FileIdentity? owned = null;
        FileStream? output = null;
        try
        {
            using (var input = opened.Stream!)
            {
                // Exclusive: an occupant of the staging name is never written to or adopted.
                // Keep the handle through promotion and cleanup so an unlinked file's inode can't be reused.
                output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, bufferSize: 0);
                owned = ExternalRomPath.SnapshotOf(output)?.Identity ?? throw new IOException($"{partial} was created but can't be identified");
                var sha256 = await CopyAsync(input, output, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                hooks?.AfterCopy?.Invoke(partial);

                var verified = await VerifyAsync(libraryRoot, partial, owned.Value, source.Length, sha256, cancellationToken).ConfigureAwait(false);
                hooks?.BeforePromotion?.Invoke(partial);

                // The backup is only worth anything while it is still the verified copy of the ZIP the move opened.
                if (ExternalRomPath.ClassifyFile(libraryRoot, partial).Snapshot != verified)
                    throw new IOException($"{partial} is no longer the verified backup");
                if (ExternalRomPath.SnapshotOf(input) != source || ExternalRomPath.ClassifyFile(libraryRoot, zipPath).Snapshot != source)
                    throw new IOException($"{zipPath} changed while it was backed up");

                var renamed = NoReplaceRename.Move(partial, backup);
                if (!renamed.Renamed)
                    throw new IOException($"{backup} can't be published: {renamed.Details ?? renamed.Status.ToString()}");
                hooks?.AfterPromotion?.Invoke(backup);
                if (ExternalRomPath.ClassifyFile(libraryRoot, backup).Snapshot != verified)
                {
                    owned = null; // the rename happened: whatever is at the staging name now isn't ours
                    throw new IOException($"{backup} is no longer the verified backup");
                }
                return new ZipBackup(backup, verified, sha256);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfOwned(libraryRoot, partial, owned);
            throw ex as IOException ?? new IOException(ex.Message, ex);
        }
        finally
        {
            output?.Dispose();
        }
    }

    private static async Task<string> CopyAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Reopens the staging file through a checked handle: it must be the file this backup created, with the copy's length and SHA256.</summary>
    private static async Task<ExternalFileSnapshot> VerifyAsync(string libraryRoot, string partial, FileIdentity owned, long length, string sha256,
        CancellationToken cancellationToken)
    {
        var reopened = ExternalRomPath.OpenRead(libraryRoot, partial, FileShare.ReadWrite | FileShare.Delete);
        if (reopened.Status != ExternalPathStatus.Found || reopened.Snapshot!.Identity != owned)
        {
            reopened.Stream?.Dispose();
            throw new IOException($"{partial} is no longer the file this backup created");
        }
        using var stream = reopened.Stream!;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            total += read;
        }
        if (total != length || Convert.ToHexStringLower(sha.GetHashAndReset()) != sha256 || ExternalRomPath.SnapshotOf(stream) != reopened.Snapshot)
            throw new IOException($"{partial} doesn't read back as the copy that was written");
        return reopened.Snapshot;
    }

    private static void DeleteIfOwned(string libraryRoot, string partial, FileIdentity? owned)
    {
        try
        {
            if (owned is not null && ExternalRomPath.ClassifyFile(libraryRoot, partial).Snapshot?.Identity == owned)
                File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The backup's own failure is the one to report; a leftover .partial is never taken for a backup.
        }
    }
}
