using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Ingest;

/// <summary>Test seams of <see cref="IngestPipeline"/>'s external path: points where a test changes a file mid-ingest.</summary>
public sealed class ExternalIngestHooks
{
    /// <summary>Runs once an external source is open and checked, right before it is hashed.</summary>
    public Action<IngestFile>? BeforeHash { get; init; }

    /// <summary>Wraps the stream an external source is hashed from, e.g. to pause mid-read.</summary>
    public Func<Stream, Stream>? WrapHashStream { get; init; }

    /// <summary>Passed to the move that sets an external source aside into <c>_failed/</c> or <c>_duplicates/</c>.</summary>
    public ExternalMoveHooks? Aside { get; init; }

    /// <summary>Runs after media and naming, right before the source is checked again and moved.</summary>
    public Action<IngestFile>? BeforePlacement { get; init; }

    /// <summary>Passed to the move that places the ROM file next to its ZIP (not to a rollback).</summary>
    public ExternalMoveHooks? Move { get; init; }

    /// <summary>Passed to the move that sets a self-repair's incoming copy aside when its archive changed (I5).</summary>
    public ExternalMoveHooks? Recovery { get; init; }

    /// <summary>Replaces the native rename when a repair sets aside or restores the original ROM.</summary>
    public Func<string, string, NoReplaceRenameResult>? RepairRename { get; init; }

    /// <summary>Passed to the ZIP write that follows the move.</summary>
    public LibraryZipWriterHooks? Writer { get; init; }
}
