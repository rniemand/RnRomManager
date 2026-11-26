using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>A headerless external handler that records how many bytes each <c>ReadHeader</c> call got.</summary>
internal sealed class Handler(string id, string[] extensions, long maxBytes = 64L * 1024 * 1024) : ISystemHandler
{
    public List<int> HeaderLengths { get; } = [];
    public string Id => id;
    public string DisplayName => id;
    public string Family => "ext";
    public IReadOnlyList<string> RomExtensions => extensions;
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => maxBytes;
    public string DatName => "Test - " + id;
    public bool EmbedRomsInZip => false;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        HeaderLengths.Add(rom.Length);
        return null;
    }
}
