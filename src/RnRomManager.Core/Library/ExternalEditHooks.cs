using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>Test seams of <see cref="LibraryEditor"/>'s external edits.</summary>
public sealed class ExternalEditHooks
{
    /// <summary>Passed to every move of an edit, including the moves that undo it.</summary>
    public ExternalMoveHooks? Move { get; init; }

    /// <summary>Passed to every checked ZIP write of an edit, including a target's undo.</summary>
    public LibraryZipWriterHooks? Writer { get; init; }

    /// <summary>Passed to a move's source backup and its recovery record.</summary>
    public ExternalZipBackupHooks? Backup { get; init; }

    /// <summary>Runs after a move published its target, right before it checks that target and rebuilds the source.</summary>
    public Action? BeforeSourceRebuild { get; init; }

    /// <summary>Runs after a move wrote its ZIPs, right before it checks them and updates the DB.</summary>
    public Action? BeforeMirror { get; init; }
}
