namespace RnRomManager.Systems.GameBoy;

internal sealed record Tables(
    IReadOnlyDictionary<byte, string> CartridgeTypes,
    IReadOnlyDictionary<byte, string> OldLicensees,
    IReadOnlyDictionary<string, string> NewLicensees);
