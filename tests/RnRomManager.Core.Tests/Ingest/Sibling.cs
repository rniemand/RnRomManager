using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>A same-family sibling of <see cref="ExternalTestHandler"/> with its own extensions and size cap.</summary>
internal sealed class Sibling(IReadOnlyList<string> extensions, long maxBytes) : ISystemHandler
{
    public string Id => "ext2";
    public string DisplayName => "External Sibling";
    public string Family => ExternalTestHandler.SystemId;
    public IReadOnlyList<string> RomExtensions => extensions;
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => maxBytes;
    public string DatName => "Test - External Sibling";
    public bool EmbedRomsInZip => false;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
