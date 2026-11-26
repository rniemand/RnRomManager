namespace RnRomManager.Core.Bundles;

/// <summary>
/// One file in a cart or saved queue (B3): the stable identity (<see cref="GameId"/>, <see cref="ReleaseSha1"/> and,
/// for a set file, <see cref="Member"/>) plus its last-known labels. A main-file entry has a null <see cref="Member"/>.
/// </summary>
public sealed record BundleEntry(
    Guid GameId,
    string ReleaseSha1,
    BundleMember? Member,
    string GameTitle,
    string FileName);
