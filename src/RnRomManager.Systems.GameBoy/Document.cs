namespace RnRomManager.Systems.GameBoy;

internal sealed record Document(
    Dictionary<string, string> CartridgeTypes,
    Dictionary<string, string> OldLicensees,
    Dictionary<string, string> NewLicensees);
