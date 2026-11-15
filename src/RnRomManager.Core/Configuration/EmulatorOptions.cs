namespace RnRomManager.Core.Configuration;

/// <summary>Optional browser-play assets the deployment operator supplies (Genesis B4, 32X B1, SNES B2, 3DO B5).</summary>
public sealed class EmulatorOptions
{
    public const string Section = "Emulator";

    /// <summary>
    /// A fully qualified, read-only directory holding the EmulatorJS 4.2.3 Genesis Plus GX files at its root. Empty
    /// disables Genesis play; a missing directory behaves as unavailable without failing startup.
    /// </summary>
    public string GenesisCorePath { get; set; } = "";

    /// <summary>
    /// B1: a fully qualified, read-only directory holding the EmulatorJS 4.2.3 PicoDrive files at its root (it may be
    /// the Genesis folder). Empty disables 32X play; a missing directory behaves as unavailable without failing startup.
    /// </summary>
    public string PicodriveCorePath { get; set; } = "";

    /// <summary>
    /// SNES B2: a fully qualified, read-only directory holding the EmulatorJS 4.2.3 Snes9x files at its root (it may be
    /// shared with the other operator cores). Empty disables SNES play; a missing directory behaves as unavailable
    /// without failing startup.
    /// </summary>
    public string Snes9xCorePath { get; set; } = "";

    /// <summary>
    /// 3DO B5: a fully qualified, read-only directory holding the EmulatorJS 4.2.3 Opera files at its root (it may be
    /// shared with the other operator cores). Empty disables 3DO play; a missing directory behaves as unavailable
    /// without failing startup.
    /// </summary>
    public string OperaCorePath { get; set; } = "";
}
