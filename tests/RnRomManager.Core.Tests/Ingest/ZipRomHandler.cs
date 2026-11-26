using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>MS-DOS spec I1: an external no-DAT system whose ROMs are <c>.zip</c> files.</summary>
internal sealed class ZipRomHandler : ISystemHandler
{
    public string Id => "zipx";
    public string DisplayName => "Zip X";
    public string Family => "zipx";
    public IReadOnlyList<string> RomExtensions => [".dosz", ".zip"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 1024 * 1024;
    public string DatName => "Test - Zip X";
    public bool EmbedRomsInZip => false;
    public DatSource DatSource => DatSource.None;
    public IReadOnlyList<string> MetadatKinds => [];
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
