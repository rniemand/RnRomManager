using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Tests.Integration;

internal sealed class IngestReviewHeaderless(string id) : ISystemHandler
{
    public string Id => id;
    public string DisplayName => id;
    public string Family => "gameboy";
    public IReadOnlyList<string> RomExtensions => ["." + id];
    public long MinRomSizeBytes => 0x150;
    public long MaxRomSizeBytes => 16 * 1024 * 1024;
    public string DatName => id == "gb" ? IngestReviewTests.Gb : "Nintendo - Game Boy Color";
    public bool EmbedRomsInZip => true;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ReadOnlyMemory<byte>.Empty;
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
