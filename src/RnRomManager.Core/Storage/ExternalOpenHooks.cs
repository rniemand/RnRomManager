namespace RnRomManager.Core.Storage;

/// <summary>Test seams of <see cref="ExternalRomPath.OpenRead"/>: points where a test swaps an entry mid-open.</summary>
public sealed class ExternalOpenHooks
{
    /// <summary>Runs after the path was classified and before anything is opened.</summary>
    public Action? AfterClassify { get; init; }

    /// <summary>Runs with each directory's full path right before that directory is opened.</summary>
    public Action<string>? BeforeDirectoryOpen { get; init; }
}
