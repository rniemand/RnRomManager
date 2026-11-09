namespace RnRomManager.Core.Collections;

/// <summary>A write hit the per-user unique key on <c>collections.name</c> (B3).</summary>
public sealed class CollectionNameTakenException(string name, Exception? inner = null)
    : Exception($"Collection name '{name}' is already taken.", inner);
