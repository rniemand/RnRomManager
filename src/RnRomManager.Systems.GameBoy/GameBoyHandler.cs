namespace RnRomManager.Systems.GameBoy;

/// <summary>Original Game Boy carts (CGB flag bit 7 clear).</summary>
public sealed class GameBoyHandler : GameBoyHandlerBase
{
    public override string Id => GameBoyHeaderReader.GameBoyId;
    public override string DisplayName => "Game Boy";
    public override string ShortName => "GB";
    public override IReadOnlyList<string> RomExtensions { get; } = [".gb"];
    public override string DatName => "Nintendo - Game Boy";
}
