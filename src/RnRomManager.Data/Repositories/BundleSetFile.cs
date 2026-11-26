namespace RnRomManager.Data.Repositories;

internal sealed record BundleSetFile(
    long Id,
    long RomId,
    string Sha1,
    string FileName);
