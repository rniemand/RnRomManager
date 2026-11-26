using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>Test seams of <see cref="ExternalZipBackup.CreateAsync"/>: points where a test swaps a file mid-backup.</summary>
public sealed class ExternalZipBackupHooks
{
    /// <summary>Runs once the copy is flushed, before it is reopened to be verified.</summary>
    public Action<string>? AfterCopy { get; init; }

    /// <summary>Runs with the staging path once the copy is verified, right before the checks that precede promotion.</summary>
    public Action<string>? BeforePromotion { get; init; }

    /// <summary>Runs with the backup path right after the rename that published it, before it is checked there.</summary>
    public Action<string>? AfterPromotion { get; init; }

    /// <summary>Replaces the no-replace rename that publishes the backup and its record (e.g. to report <c>Unsupported</c>).</summary>
    public Func<string, string, NoReplaceRenameResult>? Rename { get; init; }
}
