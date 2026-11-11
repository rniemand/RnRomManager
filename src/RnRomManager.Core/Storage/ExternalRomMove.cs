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

    /// <summary>Runs with the staging path once the file was renamed to it, before it is checked.</summary>
    public Action<string>? AfterStaging { get; init; }
}

/// <summary>
/// Moves one external ROM file within a filesystem (spec W6), for ingest placement, edits and their undo alike:
/// the file is renamed to the destination's staging name, checked there against the snapshot the caller verified,
/// then renamed to the destination. Neither rename replaces anything, so an occupied name is never overwritten and
/// a file that isn't the expected one is never promoted. To undo a move, call it again from the promoted path
/// with the returned snapshot.
/// </summary>
public static class ExternalRomMove
{
    /// <param name="source">Fully qualified.</param>
    /// <param name="destination">Fully qualified; its folder exists and was checked by the caller.</param>
    /// <param name="expected">What <paramref name="source"/> must still be: the hash-time snapshot, or a promoted file's snapshot for an undo.</param>
    public static ExternalMoveResult Move(string source, string destination, ExternalFileSnapshot expected, ILogger logger,
        ExternalMoveHooks? hooks = null)
    {
        var rename = hooks?.Rename ?? NoReplaceRename.Move;
        if (NativeFileStat.Read(source).Snapshot != expected)
            return new ExternalMoveResult(ExternalMoveStatus.SourceChanged, Details: $"{source} is not the file that was checked");

        var staging = Path.Combine(Path.GetDirectoryName(destination)!, ExternalRomName.StagingName(Path.GetFileName(destination)));
        var staged = rename(source, staging);
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

        var promoted = rename(staging, destination);
        if (!promoted.Renamed)
        {
            var failure = Failed(promoted, staging, destination, logger);
            return PutBack(staging, source, ExternalMoveStatus.Failed, failure.Details!, rename);
        }
        return NativeFileStat.Read(destination).Snapshot == expected
            ? new ExternalMoveResult(ExternalMoveStatus.Moved, destination, expected)
            : new ExternalMoveResult(ExternalMoveStatus.Failed, Details: $"{destination} is not the file that was moved there");
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
        var details = result.Status switch
        {
            NoReplaceRenameStatus.Unsupported => NoReplaceRename.UnsupportedDetails(destination),
            // shortcut: no copy across volumes yet, step 8a of the external ROM spec adds it.
            _ => result.Details ?? $"moving {from} to {destination} failed",
        };
        if (result.Status == NoReplaceRenameStatus.Unsupported)
            logger.LogError("External ROM move of {Source}: {Details}", from, details);
        return new ExternalMoveResult(ExternalMoveStatus.Failed, Details: details, SurvivingPath: from);
    }
}
