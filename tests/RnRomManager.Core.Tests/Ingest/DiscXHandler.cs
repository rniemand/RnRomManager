using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>
/// A FileSets system that takes no .cue: nothing is planned there. A class, not a substitute: Castle proxies can't
/// intercept <see cref="ISystemHandler.ReadHeader"/>'s span parameter, which the external path calls.
/// </summary>
internal sealed class DiscXHandler : ISystemHandler
{
    public string Id => "discx";
    public string DisplayName => "Disc X";
    public string Family => "discx";
    public IReadOnlyList<string> RomExtensions => [".bin"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 1024 * 1024;
    public string DatName => "Test - Disc X";
    public bool EmbedRomsInZip => false;
    public bool FileSets => true;
    public string DatFolder => DatKind.Redump;
    public IReadOnlyList<string> MetadatKinds => [];
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
