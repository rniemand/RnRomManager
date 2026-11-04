namespace RnRomManager.Systems.GameBoy;

/// <summary>GBC-only and dual-mode carts (CGB flag bit 7 set, D1/D23).</summary>
public sealed class GameBoyColorHandler : GameBoyHandlerBase
{
    public override string Id => GameBoyHeaderReader.GameBoyColorId;
    public override string DisplayName => "Game Boy Color";
    public override IReadOnlyList<string> RomExtensions { get; } = [".gbc"];
    public override string DatName => "Nintendo - Game Boy Color";
}
