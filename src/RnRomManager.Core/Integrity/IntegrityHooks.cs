namespace RnRomManager.Core.Integrity;

/// <summary>Test seams of <see cref="IntegrityJob"/>'s external file checks.</summary>
public sealed class IntegrityHooks
{
    /// <summary>Runs with the file's path after its stat, right before a Deep check opens it.</summary>
    public Action<string>? BeforeExternalHash { get; init; }

    /// <summary>Runs with the file's path after it was hashed and its handle closed, before the path is checked again.</summary>
    public Action<string>? AfterExternalHash { get; init; }

    /// <summary>Wraps the stream a Deep check hashes an external ROM from, e.g. to pause mid-read.</summary>
    public Func<Stream, Stream>? WrapHashStream { get; init; }
}
