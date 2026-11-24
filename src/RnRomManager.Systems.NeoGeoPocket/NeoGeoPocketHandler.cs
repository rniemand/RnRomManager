namespace RnRomManager.Systems.NeoGeoPocket;

/// <summary>Mono Neo Geo Pocket carts (header mode byte not 0x10), <c>.ngp</c>.</summary>
public sealed class NeoGeoPocketHandler : NeoGeoPocketHandlerBase
{
    public override string Id => NeoGeoPocketHeaderReader.NeoGeoPocketId;
    public override string DisplayName => "Neo Geo Pocket";
    public override string ShortName => "NGP";
    public override IReadOnlyList<string> RomExtensions { get; } = [".ngp"];
    public override string DatName => "SNK - Neo Geo Pocket";
}
