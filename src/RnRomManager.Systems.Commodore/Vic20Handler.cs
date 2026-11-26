using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore VIC-20. Matched against libretro's No-Intro <c>Commodore - VIC-20</c> DAT: raw cartridge dumps of 4–16 KiB
/// whose extension names the load address (<c>.20</c>, <c>.40</c>, <c>.60</c>, <c>.a0</c>; one <c>.bin</c>), so Review keeps
/// the source extension, which VICE needs to map the cartridge. The dumps have no header, so <see cref="ReadHeader"/>
/// always returns null and an unmatched file goes to Review. Programs, disk images, tapes and VICE <c>.crt</c> cartridges
/// (<c>.prg</c>, <c>.d64</c>, <c>.tap</c>, <c>.t64</c>, <c>.crt</c>, <c>.d71</c>, <c>.d81</c>, <c>.g64</c>, <c>.x64</c>,
/// <c>.p00</c>) and <c>.b0</c>/<c>.rom</c> cartridges aren't in the DAT, so they come in through Review; hence
/// the 256 B floor (small programs) and 8 MiB cap (tapes).
/// </summary>
public sealed class Vic20Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(Vic20Handler), "boxart.vic20.png");

    public string Id => "vic20";
    public string DisplayName => "Commodore VIC-20";
    public string ShortName => "VIC-20";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".a0", ".20", ".40", ".60", ".bin", ".prg", ".d64", ".tap", ".t64", ".crt", ".b0", ".rom", ".d71", ".d81", ".g64", ".x64", ".p00", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z", ".gz"];
    public long MinRomSizeBytes => 256;
    public long MaxRomSizeBytes => 8 * 1024 * 1024;
    public string DatName => "Commodore - VIC-20";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>VICE (xvic), whose files the operator supplies next to the C64 ones (<c>Emulator:ViceCorePath</c>).</summary>
    public string BrowserCore => "vice_xvic";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    /// <summary>V2: a gzipped image is inflated, so it's hashed, header-read and stored as the image it holds.</summary>
    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => GzipImage.Inflate(rom, MaxRomSizeBytes);

    /// <summary>V3: a gzipped image is stored under the type it holds (<c>.d6z</c> → <c>.d64</c>, <c>Game.d64.gz</c> → <c>.d64</c>).</summary>
    public string? SourceExtension(string fileName) => ViceImages.SourceExtension(fileName, RomExtensions);
}
