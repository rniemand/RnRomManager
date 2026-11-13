using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

public sealed class AtariJaguarHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.jaguar.png"));

    public string Id => "jaguar";
    public string DisplayName => "Atari Jaguar";
    public string ShortName => "Jaguar";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".j64", ".jag", ".rom", ".abs", ".cof"];
    public long MinRomSizeBytes => 16 * 1024;
    public long MaxRomSizeBytes => 16 * 1024 * 1024;
    public string DatName => "Atari - Jaguar";
    public bool EmbedRomsInZip => true;
    public string BrowserCore => "virtualjaguar";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
