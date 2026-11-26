using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore 64 (spec B1). Each extension names a distinct format (tape, disk image, cartridge, program), so Review keeps
/// the source extension. <see cref="ReadHeader"/> recognises the optional CRT/TAP/G64/NIB container headers and returns
/// null otherwise, so an unmatched file goes to Review. Sizes follow the libretro No-Intro <c>Commodore - 64</c> DAT
/// (2,010 B–4.19 MB) with room for homebrew below and headroom above. <c>.d71</c>, <c>.d81</c>, <c>.x64</c>, <c>.p00</c>, <c>.d80</c> and <c>.d82</c>
/// aren't in the DAT: VICE opens them, so they come in through Review, as do NIBTools <c>.nbz</c>, CMD <c>.d2m</c>/<c>.d4m</c> and
/// <c>.tcrt</c>. Gzipped disk images (<c>.d6z</c>, <c>.d7z</c>, <c>.d8z</c>, <c>.g6z</c>, <c>.x6z</c>, <c>Game.d64.gz</c>) are inflated.
/// </summary>
public sealed class C64Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(C64Handler), "boxart.c64.png");

    public string Id => "c64";
    public string DisplayName => "Commodore 64";
    public string ShortName => "C64";
    public string Family => Id;

    /// <summary><c>[0]</c> (<c>.crt</c>) is the first extension; Review keeps the source's own (<see cref="KeepsSourceExtension"/>).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".crt", ".tap", ".nib", ".d64", ".g64", ".t64", ".prg", ".bin", ".d71", ".d81", ".x64", ".p00", ".d80", ".d82", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z", ".gz", ".nbz", ".d2m", ".d4m", ".tcrt"];

    public long MinRomSizeBytes => 1024;
    public long MaxRomSizeBytes => 8 * 1024 * 1024;
    public string DatName => "Commodore - 64";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>C64 B7: VICE (x64sc), whose files the operator supplies (<c>Emulator:ViceCorePath</c>).</summary>
    public string BrowserCore => "vice_x64sc";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => C64Header.Read(rom, Id);
    /// <summary>V2: a gzipped image is inflated, so it's hashed, header-read and stored as the image it holds.</summary>
    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => GzipImage.Inflate(rom, MaxRomSizeBytes);

    /// <summary>V3: a gzipped image is stored under the type it holds (<c>.d6z</c> → <c>.d64</c>, <c>Game.d64.gz</c> → <c>.d64</c>).</summary>
    public string? SourceExtension(string fileName) => ViceImages.SourceExtension(fileName, RomExtensions);
}
