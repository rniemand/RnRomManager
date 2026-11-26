namespace RnRomManager.Data.Repositories;

internal sealed record EntryRow(
    string Key,
    byte[] Norm,
    byte[] Trimmed,
    Guid GameId);
