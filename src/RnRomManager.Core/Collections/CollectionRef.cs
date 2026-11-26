namespace RnRomManager.Core.Collections;

/// <summary>A collection's id and name, for a game's collections (B16).</summary>
public sealed record CollectionRef(Guid Id, string Name);
