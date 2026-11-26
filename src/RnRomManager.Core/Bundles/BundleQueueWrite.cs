namespace RnRomManager.Core.Bundles;

/// <summary>The outcome of a queue write (B4). <see cref="Queue"/> is set only for <see cref="BundleQueueWriteStatus.Ok"/>.</summary>
public sealed record BundleQueueWrite(BundleQueueWriteStatus Status, BundleQueue? Queue = null)
{
    public static readonly BundleQueueWrite NotFound = new(BundleQueueWriteStatus.NotFound);
    public static readonly BundleQueueWrite Changed = new(BundleQueueWriteStatus.Changed);
    public static readonly BundleQueueWrite NameTaken = new(BundleQueueWriteStatus.NameTaken);
    public static readonly BundleQueueWrite Limit = new(BundleQueueWriteStatus.Limit);
}
