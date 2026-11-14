using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Atari;

/// <summary>
/// Atari 7800. The libretro <c>Atari - 7800</c> DAT lists headered <c>.a78</c> and headerless <c>.bin</c> dumps separately,
/// each matching its own entry, so the optional <c>ATARI7800</c> header is read but never stripped for hashing.
/// </summary>
public sealed class Atari7800Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.a7800.png"));

    public string Id => "a7800";
    public string DisplayName => "Atari 7800";
    public string ShortName => "7800";
    public string Family => Id;

    /// <summary><c>[0]</c> (<c>.a78</c>) is what a file resolved through Review without a DAT entry is stored as, even a headerless one.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".a78", ".bin"];

    public long MinRomSizeBytes => 4 * 1024;

    public long MaxRomSizeBytes => 1024 * 1024;

    public string DatName => "Atari - 7800";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => A78Header.Read(rom);
}
