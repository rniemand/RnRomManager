namespace RnRomManager.Core.Bundles;

/// <summary>
/// A file's stable identity (B3): the game, the release by its globally unique <c>roms.sha1</c> and, for a set file, its
/// SHA-1 plus stored file name within that release. A main file has a null <see cref="Member"/>.
/// </summary>
public sealed record BundleFileIdentity(Guid GameId, string ReleaseSha1, BundleMember? Member);
