namespace RnRomManager.Core.Configuration;

/// <summary>Optional browser-play assets the deployment operator supplies (Genesis B4, 32X B1, SNES B2, 3DO B5, C64 B7, N64).</summary>
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

    /// <summary>
    /// C64 B7: a fully qualified, read-only directory holding the EmulatorJS 4.2.3 VICE (x64sc) files at its root (it may
    /// be shared with the other operator cores). Never bundled: the binary embeds Commodore's system ROMs. Empty disables
    /// Commodore 64 play; a missing directory behaves as unavailable without failing startup.
    /// </summary>
    public string ViceCorePath { get; set; } = "";

    /// <summary>
    /// N64: a fully qualified, read-only directory holding the EmulatorJS 4.2.3 Mupen64Plus-Next files at its root (it may
    /// be shared with the other operator cores). Never bundled: its GLideN64 renderer is GPL-2.0-only, which can't be
    /// combined with the GPL-3.0 RetroArch frontend it's linked with. Empty disables Nintendo 64 play; a missing directory
    /// behaves as unavailable without failing startup.
    /// </summary>
    public string Mupen64PlusCorePath { get; set; } = "";

    /// <summary>
    /// Arcade P1: a fully qualified, read-only directory holding the EmulatorJS 4.2.3 MAME 2003-Plus files at its root (it may
    /// be shared with the other operator cores). Never bundled: the MAME licence forbids commercial use. Empty disables
    /// Arcade play; a missing directory behaves as unavailable without failing startup.
    /// </summary>
    public string Mame2003PlusCorePath { get; set; } = "";
}
