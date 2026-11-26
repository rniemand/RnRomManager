using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Tests.Ingest;

internal sealed class Embedded : ISystemHandler
{
    public string Id => "gb";
    public string DisplayName => "Test";
    public string Family => "gameboy";
    public IReadOnlyList<string> RomExtensions => [".gb"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 1024;
    public string DatName => "GB";
    public bool EmbedRomsInZip => true;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => Array.Empty<byte>();
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
