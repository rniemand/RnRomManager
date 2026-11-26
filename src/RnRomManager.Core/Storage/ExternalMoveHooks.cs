namespace RnRomManager.Core.Storage;

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
