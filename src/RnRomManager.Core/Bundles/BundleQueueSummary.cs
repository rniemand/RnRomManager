namespace RnRomManager.Core.Bundles;

/// <summary>A saved queue in the list (B4: <c>{ id, name, revision, itemCount }</c>).</summary>
public sealed record BundleQueueSummary(
    Guid Id,
    string Name,
    int Revision,
    int ItemCount);
