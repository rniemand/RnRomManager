using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Tests.Ingest;

internal sealed class ReviewResolverHeaderless(params string[] extensions) : ISystemHandler
{
    public string Id => "gb";
    public string DisplayName => "Test";
    public string Family => "gameboy";
    public IReadOnlyList<string> RomExtensions => extensions.Length == 0 ? [".gb"] : extensions;
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 1024;
    public string DatName => "GB";
    public bool EmbedRomsInZip => true;
    public bool KeepsSourceExtension { get; init; }
    public Func<string, string?>? MapSourceExtension { get; init; }
    public string? SourceExtension(string fileName) => MapSourceExtension?.Invoke(fileName) ?? Path.GetExtension(fileName).ToLowerInvariant();
    // A 512×512 PNG header: enough for MediaService's default-art check when a resolve stores a new game.
    public ReadOnlyMemory<byte> DefaultBoxArtPng { get; } = new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 2, 0, 0, 0, 2, 0,
        8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    };
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
