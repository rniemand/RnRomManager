namespace RnRomManager.Core.Naming;

/// <summary>The tags of a release that release editing (B3) can change and that show in its file name.</summary>
public sealed record ReleaseTags(
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    string? Revision,
    bool Hack,
    bool Translated,
    string? Version);
