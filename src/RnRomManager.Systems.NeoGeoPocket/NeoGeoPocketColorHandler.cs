namespace RnRomManager.Systems.NeoGeoPocket;

/// <summary>Neo Geo Pocket Color carts (header mode byte 0x10); <c>.ngc</c> is the DAT's extension, the others are ingest aliases.</summary>
public sealed class NeoGeoPocketColorHandler : NeoGeoPocketHandlerBase
{
    public override string Id => NeoGeoPocketHeaderReader.NeoGeoPocketColorId;
    public override string DisplayName => "Neo Geo Pocket Color";
    public override string ShortName => "NGPC";
    public override IReadOnlyList<string> RomExtensions { get; } = [".ngc", ".ngpc", ".npc"];
    public override string DatName => "SNK - Neo Geo Pocket Color";
}
