using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Core.Storage;

public enum ExternalMoveStatus
{
    /// <summary>The file is at the destination; <see cref="ExternalMoveResult.Snapshot"/> describes it there.</summary>
    Moved,

    /// <summary>
    /// The file at the source is not the expected one (replaced, changed, or gone). It was left, or put back, at
    /// the source; nothing reached the destination.
    /// </summary>
    SourceChanged,

    /// <summary>An <c>IoError</c>: see <see cref="ExternalMoveResult.Details"/> and <see cref="ExternalMoveResult.SurvivingPath"/>.</summary>
    Failed,
}

/// <param name="Path">The destination, for <see cref="ExternalMoveStatus.Moved"/>.</param>
/// <param name="Snapshot">The promoted file, for <see cref="ExternalMoveStatus.Moved"/>; pass it to every later check, undo or cleanup of that path.</param>
/// <param name="SurvivingPath">
/// For <see cref="ExternalMoveStatus.Failed"/>: where the complete, owned copy is now (the source when nothing
/// moved, a staging name when putting it back failed); null when it could not be found.
/// </param>
public sealed record ExternalMoveResult(
    ExternalMoveStatus Status, string? Path = null, ExternalFileSnapshot? Snapshot = null, string? Details = null, string? SurvivingPath = null);

/// <summary>Test seams of <see cref="ExternalRomMove.Move"/>.</summary>
public sealed class ExternalMoveHooks
{
    /// <summary>Replaces <see cref="NoReplaceRename.Move"/>.</summary>
    public Func<string, string, NoReplaceRenameResult>? Rename { get; init; }

    /// <summary>Runs with the staging path once the file was renamed or copied to it, before it is checked.</summary>
    public Action<string>? AfterStaging { get; init; }

    /// <summary>Wraps the source stream of a staged copy, e.g. to fail part-way.</summary>
    public Func<Stream, Stream>? WrapSource { get; init; }

    /// <summary>Replaces <see cref="File.Delete"/> for the source of a staged copy.</summary>
    public Action<string>? DeleteSource { get; init; }

}

/// <summary>
/// Moves one external ROM file (spec W6), for ingest placement, edits and their undo alike: the file is renamed to
/// the destination's staging name, checked there against the snapshot the caller verified, then renamed to the
/// destination. Neither rename replaces anything, so an occupied name is never overwritten and a file that isn't
/// the expected one is never promoted. If the first rename crosses volumes or is unsupported, the file is copied
/// to the staging name instead. Publication can use a hard link if the final rename is unsupported.
/// The source of a staged copy is deleted once the verified copy is published and the source path
/// still names the checked file. To undo a move, call it again from the promoted path with the returned snapshot.
/// </summary>
public static class ExternalRomMove
{
    /// <param name="sourceRoot">The storage root <paramref name="source"/> is under (a copy reads it through the checked open).</param>
    /// <param name="source">Fully qualified.</param>
    /// <param name="destination">Fully qualified; its folder exists and was checked by the caller.</param>
    /// <param name="expected">What <paramref name="source"/> must still be: the hash-time snapshot, or a promoted file's snapshot for an undo.</param>
    /// <param name="expectedSha1">The verified SHA1 of the incoming bytes, when the caller has one: a copy must reproduce it.</param>
    public static ExternalMoveResult Move(string sourceRoot, string source, string destination,
        ExternalFileSnapshot expected, ILogger logger, string? expectedSha1 = null, ExternalMoveHooks? hooks = null)
    {
        var rename = hooks?.Rename ?? NoReplaceRename.Move;
        if (NativeFileStat.Read(source).Snapshot != expected)
            return new ExternalMoveResult(ExternalMoveStatus.SourceChanged, Details: $"{source} is not the file that was checked");

        var staging = Path.Combine(Path.GetDirectoryName(destination)!, ExternalRomName.StagingName(Path.GetFileName(destination)));
        var staged = rename(source, staging);
        if (staged.Status is NoReplaceRenameStatus.CrossDevice or NoReplaceRenameStatus.Unsupported)
            return CopyAndPublish(sourceRoot, source, destination, staging, expected, logger, expectedSha1, hooks);
        if (!staged.Renamed)
            return Failed(staged, source, destination, logger);
        hooks?.AfterStaging?.Invoke(staging);

        var atStaging = NativeFileStat.Read(staging);
        if (atStaging.Outcome != NativeStatOutcome.Found)
        {
            // Never claim whatever is around as the verified copy.
            return new ExternalMoveResult(ExternalMoveStatus.Failed,
                Details: $"the copy of {source} staged at {staging} can't be found there: {atStaging.Error ?? "it is missing"}");
        }
        if (atStaging.Snapshot != expected)
            return PutBack(staging, source, ExternalMoveStatus.SourceChanged, $"{source} changed while it was being moved", rename);

        var promoted = NoReplaceRename.PublishStaged(staging, destination, expected, rename);
        if (!promoted.Renamed)
        {
            var failure = Failed(promoted, staging, destination, logger);
            return PutBack(staging, source, ExternalMoveStatus.Failed, failure.Details!, rename);
        }
        return NativeFileStat.Read(destination).Snapshot == expected
            ? new ExternalMoveResult(ExternalMoveStatus.Moved, destination, expected)
            : new ExternalMoveResult(ExternalMoveStatus.Failed, Details: $"{destination} is not the file that was moved there");
    }

    private static ExternalMoveResult CopyAndPublish(string sourceRoot, string source, string destination, string staging,
        ExternalFileSnapshot expected, ILogger logger, string? expectedSha1, ExternalMoveHooks? hooks)
    {
        ExternalMoveResult Fail(string details, string? survivor) => new(ExternalMoveStatus.Failed, Details: details, SurvivingPath: survivor);
        ExternalMoveResult Changed() => new(ExternalMoveStatus.SourceChanged, Details: $"{source} is not the file that was checked", SurvivingPath: source);

        var opened = ExternalRomPath.OpenRead(sourceRoot, source, FileShare.Read | FileShare.Delete, expected);
        if (opened.Status is ExternalPathStatus.Changed or ExternalPathStatus.Missing)
            return Changed();
        if (opened.Status != ExternalPathStatus.Found)
            return Fail(opened.Details ?? $"{source} can't be opened", source);

        FileIdentity? identity;
        string? sha1 = null;
        using (var raw = opened.Stream!)
        {
            FileStream output;
            try
            {
                output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"{staging} can't be created: {ex.Message}", source); // an occupant is left as it is
            }

            identity = ExternalRomPath.SnapshotOf(output)?.Identity;
            try
            {
                using (output)
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
                {
                    var input = hooks?.WrapSource?.Invoke(raw) ?? raw;
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (expectedSha1 is not null)
                            hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                    }
                    output.Flush(flushToDisk: true); // the copy must be on disk before publication and source deletion
                    sha1 = Convert.ToHexStringLower(hash.GetHashAndReset());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteOwned(staging, identity, logger);
                return Fail($"copying {source} to {staging} failed: {ex.Message}", source);
            }
        }

        hooks?.AfterStaging?.Invoke(staging);
        var owned = NativeFileStat.Read(staging).Snapshot;
        if (owned is null || identity is null || owned.Identity != identity)
            return Fail($"the copy of {source} staged at {staging} was replaced or is gone", source);
        if (owned.Length != expected.Length || (expectedSha1 is not null && !string.Equals(sha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            || NativeFileStat.Read(source).Snapshot != expected)
        {
            // The handle may have read the right bytes while the path was given to another file: keep what is there.
            DeleteOwned(staging, identity, logger);
            return Changed();
        }

        if (NativeFileStat.Read(staging).Snapshot != owned)
            return Fail($"the copy of {source} staged at {staging} can't be found there", source);

        var promoted = NoReplaceRename.PublishStaged(staging, destination, owned, hooks?.Rename);
        if (!promoted.Renamed)
        {
            var details = Failed(promoted, staging, destination, logger).Details!;
            DeleteOwned(staging, identity, logger);
            return Fail(details, source);
        }

        if (NativeFileStat.Read(destination).Snapshot != owned)
            return Fail($"{destination} is not the file that was moved there", source);
        if (NativeFileStat.Read(source).Snapshot != expected)
            return Fail($"{source} changed after the copy was published", destination);

        try
        {
            (hooks?.DeleteSource ?? File.Delete)(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the source is gone or changed, the published copy may be the only verified copy.
            var details = $"{source} can't be removed after copying it: {ex.Message}";
            if (NativeFileStat.Read(source).Snapshot != expected)
                return Fail(details, NativeFileStat.Read(destination).Snapshot == owned ? destination : null);
            if (NativeFileStat.Read(destination).Snapshot == owned)
                DeleteOwned(destination, identity, logger);
            if (NativeFileStat.Read(destination).Snapshot == owned)
                details += $"; the published copy also remains at {destination}";
            return Fail(details, source);
        }

        return NativeFileStat.Read(destination).Snapshot == owned
            ? new ExternalMoveResult(ExternalMoveStatus.Moved, destination, owned)
            : Fail($"{destination} is not the file that was moved there", survivor: null);
    }

    /// <summary>Deletes a copy only while it is still the one this move created; best effort.</summary>
    private static void DeleteOwned(string path, FileIdentity? identity, ILogger logger)
    {
        try
        {
            if (identity is not null && NativeFileStat.Read(path).Snapshot?.Identity == identity)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Copy at {Path} could not be removed: {Message}", path, ex.Message);
        }
    }

    private static ExternalMoveResult PutBack(string staging, string source, ExternalMoveStatus status, string details,
        Func<string, string, NoReplaceRenameResult> rename)
    {
        var back = rename(staging, source);
        return back.Renamed
            ? new ExternalMoveResult(status, Details: details, SurvivingPath: source)
            : new ExternalMoveResult(ExternalMoveStatus.Failed, Details: $"{details}; putting it back failed ({back.Details}), it is at {staging}",
                SurvivingPath: staging);
    }

    private static ExternalMoveResult Failed(NoReplaceRenameResult result, string from, string destination, ILogger logger)
    {
        var details = result.Details ?? $"moving {from} to {destination} failed";
        if (result.Status == NoReplaceRenameStatus.Unsupported)
            logger.LogError("External ROM move of {Source}: {Details}", from, details);
        return new ExternalMoveResult(ExternalMoveStatus.Failed, Details: details, SurvivingPath: from);
    }
}
