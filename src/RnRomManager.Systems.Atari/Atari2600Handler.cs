using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

/// <summary>
/// Atari 2600 (spec A2). Cartridges have no header, so <see cref="ReadHeader"/> is always null and an unmatched file goes
/// to Review. Extensions and sizes follow the libretro <c>Atari - 2600</c> DAT (2–64 KiB, <c>.a26</c>/<c>.bin</c>).
/// </summary>
public sealed class Atari2600Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.a2600.png"));

    public string Id => "a2600";
    public string DisplayName => "Atari 2600";
    public string ShortName => "2600";
    public string Family => "a2600";

    /// <summary><c>[0]</c> (<c>.a26</c>) is what a file resolved through Review is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".a26", ".bin"];

    public long MinRomSizeBytes => 2048;

    public long MaxRomSizeBytes => 65536;

    public string DatName => "Atari - 2600";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(Atari2600Handler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Atari.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
