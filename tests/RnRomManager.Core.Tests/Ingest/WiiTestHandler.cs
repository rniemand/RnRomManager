using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

internal sealed class WiiTestHandler : ISystemHandler
{
    public required RomHeader Header { get; set; }
    public string Id => "wii";
    public string DisplayName => "Wii";
    public string Family => "nintendo-disc";
    public IReadOnlyList<string> RomExtensions => [".iso", ".wbfs"];
    public long MinRomSizeBytes => 32 * 1024;
    public long MaxRomSizeBytes => 9L * 1024 * 1024 * 1024;
    public string DatName => "Nintendo - Wii";
    public string DatFolder => DatKind.Redump;
    public IReadOnlyList<string> MetadatKinds => [];
    public bool EmbedRomsInZip => false;
    public bool FileSets => true;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
    public RomHeader ReadHeader(ReadOnlySpan<byte> rom) => Header;
}
