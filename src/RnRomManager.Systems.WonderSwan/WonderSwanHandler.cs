namespace RnRomManager.Systems.WonderSwan;

/// <summary>Mono WonderSwan carts (footer colour byte not 0x01), <c>.ws</c>.</summary>
public sealed class WonderSwanHandler : WonderSwanHandlerBase
{
    public override string Id => WonderSwanFooterReader.WonderSwanId;
    public override string DisplayName => "WonderSwan";
    public override string ShortName => "WS";
    public override IReadOnlyList<string> RomExtensions { get; } = [".ws"];
    public override string DatName => "Bandai - WonderSwan";
}
