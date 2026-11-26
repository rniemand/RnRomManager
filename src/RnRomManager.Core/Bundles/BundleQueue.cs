namespace RnRomManager.Core.Bundles;

/// <summary>A saved queue's full snapshot (B4: <c>{ id, name, revision, entries }</c>).</summary>
public sealed record BundleQueue(
    Guid Id,
    string Name,
    int Revision,
    IReadOnlyList<BundleEntry> Entries);
