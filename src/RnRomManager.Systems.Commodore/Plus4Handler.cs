using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore Plus/4 (spec B1–B7). Matched against libretro's No-Intro <c>Commodore - Plus-4</c> DAT, which lists only
/// <c>.bin</c> cartridges; disk images, tapes, programs and <c>.crt</c> cartridges aren't in it, so they come in through
/// Review, which keeps the source extension (each names a distinct format). <see cref="ReadHeader"/> reuses
/// <see cref="C64Header"/> (VICE's <c>C16-TAPE-RAW</c> and <c>C64-TAPE-RAW</c> <c>.tap</c>, C64-signature <c>.crt</c>).
/// 256 B floor (small programs), 8 MiB cap (tapes), as on VIC-20.
/// </summary>
public sealed class Plus4Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.plus4.png"));

    public string Id => "plus4";
    public string DisplayName => "Commodore Plus/4";
    public string ShortName => "Plus/4";
    public string Family => Id;

    /// <summary>The DAT's entries are all <c>.bin</c>; Review keeps the source's own extension (<see cref="KeepsSourceExtension"/>).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".d64", ".t64", ".tap", ".prg", ".crt", ".bin"];

    public long MinRomSizeBytes => 256;
    public long MaxRomSizeBytes => 8 * 1024 * 1024;
    public string DatName => "Commodore - Plus-4";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>VICE (xplus4), whose files the operator supplies next to the C64 ones (<c>Emulator:ViceCorePath</c>).</summary>
    public string BrowserCore => "vice_xplus4";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => C64Header.Read(rom, Id);
}
