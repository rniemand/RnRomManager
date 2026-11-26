namespace RnRomManager.Data.Repositories;

internal sealed record BundleQueueRepositoryRow(
    Guid Id,
    string Name,
    int Revision,
    string Entries);
