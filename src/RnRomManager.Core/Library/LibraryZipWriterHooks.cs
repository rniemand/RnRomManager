using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>Test seams of <see cref="LibraryZipWriter.WriteCheckedAsync"/>: points where a test swaps a file mid-write.</summary>
public sealed class LibraryZipWriterHooks
{
    /// <summary>Runs once the temp ZIP is written and closed, before it is reopened for verification.</summary>
    public Action? AfterTempClosed { get; init; }

    /// <summary>Runs before each publication attempt (0 = the first), before that attempt's checks.</summary>
    public Action<int>? BeforePublicationAttempt { get; init; }

    /// <summary>Returns true to make that attempt's rename fail as a sharing violation would.</summary>
    public Func<int, bool>? FailPublicationAttempt { get; init; }

    /// <summary>Replaces the native no-replace rename for a new ZIP.</summary>
    public Func<string, string, NoReplaceRenameResult>? Rename { get; init; }
}
