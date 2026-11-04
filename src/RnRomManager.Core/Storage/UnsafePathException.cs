namespace RnRomManager.Core.Storage;

/// <summary>A computed path would leave its root (spec §9.3).</summary>
public sealed class UnsafePathException(string root, string relative, Exception? inner = null)
    : Exception($"Path '{relative}' resolves outside '{root}'.", inner)
{
    public string Root { get; } = root;
    public string Relative { get; } = relative;
}
