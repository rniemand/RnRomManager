using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

public sealed class AtariLynxHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.lynx.png"));

    public string Id => "lynx";
    public string DisplayName => "Atari Lynx";
    public string ShortName => "Lynx";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".lnx", ".lyx"];
    public long MinRomSizeBytes => 1024;
    public long MaxRomSizeBytes => 1024 * 1024;
    public string DatName => "Atari - Lynx";
    public bool EmbedRomsInZip => true;
    public string BrowserCore => "handy";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => LynxHeader.Read(rom);
}
