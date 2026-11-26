namespace RnRomManager.Core.Library;

/// <summary>A game and its stored <c>zip_path</c> spelling.</summary>
public sealed record ZipPathOwner(Guid GameId, string ZipPath);
