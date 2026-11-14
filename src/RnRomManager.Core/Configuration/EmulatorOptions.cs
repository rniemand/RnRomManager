namespace RnRomManager.Core.Configuration;

/// <summary>Optional browser-play assets the deployment operator supplies (Genesis B4).</summary>
public sealed class EmulatorOptions
{
    public const string Section = "Emulator";

    /// <summary>
    /// A fully qualified, read-only directory holding the EmulatorJS 4.2.3 Genesis Plus GX files at its root. Empty
    /// disables Genesis play; a missing directory behaves as unavailable without failing startup.
    /// </summary>
    public string GenesisCorePath { get; set; } = "";
}
