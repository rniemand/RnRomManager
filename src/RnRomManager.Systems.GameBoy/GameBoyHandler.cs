namespace RnRomManager.Systems.GameBoy;

/// <summary>Original Game Boy carts (CGB flag bit 7 clear), <c>.gb</c> or <c>.dmg</c>.</summary>
public sealed class GameBoyHandler : GameBoyHandlerBase
{
    public override string Id => GameBoyHeaderReader.GameBoyId;
    public override string DisplayName => "Game Boy";
    public override string ShortName => "GB";
    public override IReadOnlyList<string> RomExtensions { get; } = [".gb", ".dmg"];
    public override string DatName => "Nintendo - Game Boy";
}
