namespace RnRomManager.Systems.WonderSwan;

/// <summary>WonderSwan Color carts (footer colour byte 0x01), <c>.wsc</c>.</summary>
public sealed class WonderSwanColorHandler : WonderSwanHandlerBase
{
    public override string Id => WonderSwanFooterReader.WonderSwanColorId;
    public override string DisplayName => "WonderSwan Color";
    public override string ShortName => "WSC";
    public override IReadOnlyList<string> RomExtensions { get; } = [".wsc"];
    public override string DatName => "Bandai - WonderSwan Color";
}
