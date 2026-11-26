namespace RnRomManager.Api.Tests.Integration;

public sealed record LegacyStorageLegacy(
    Guid Id,
    long RomId,
    long ImageId,
    string ZipPath,
    byte[] Bytes);
