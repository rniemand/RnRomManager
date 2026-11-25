using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore 128 (spec B1–B7). libretro has no C128 DAT (<see cref="DatSource.None"/>), so every file goes to Review, as
/// on MS-DOS. Each extension names a distinct format (disk image, tape, program, cartridge), so Review keeps the source
/// extension. <see cref="ReadHeader"/> reuses <see cref="C64Header"/> (C64-signature <c>.crt</c>, <c>.tap</c>, <c>.g64</c>);
/// a native <c>C128 CARTRIDGE</c> <c>.crt</c> gets none. 256 B floor (small programs), 8 MiB cap (tapes), as on VIC-20.
/// </summary>
public sealed class C128Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.c128.png"));

    public string Id => "c128";
    public string DisplayName => "Commodore 128";
    public string ShortName => "C128";
    public string Family => Id;

    /// <summary><c>[0]</c> is never used for a rename: Review keeps the source's own (<see cref="KeepsSourceExtension"/>).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".d64", ".d71", ".d81", ".g64", ".t64", ".tap", ".prg", ".p00", ".crt"];

    public long MinRomSizeBytes => 256;
    public long MaxRomSizeBytes => 8 * 1024 * 1024;

    /// <summary>Unique among handlers; with <see cref="DatSource.None"/> it names no file.</summary>
    public string DatName => "Commodore - 128";

    /// <inheritdoc />
    public DatSource DatSource => DatSource.None;

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>VICE (x128), whose files the operator supplies next to the C64 ones (<c>Emulator:ViceCorePath</c>).</summary>
    public string BrowserCore => "vice_x128";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => C64Header.Read(rom, Id);
}
