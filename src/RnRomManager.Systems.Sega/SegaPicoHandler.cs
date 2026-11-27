using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Pico (Sega Pico P1, P2). Pico carts carry the Mega Drive header (system type e.g. <c>SEGA PICO</c>), so the
/// reader is shared; the header is optional, as for 32X. <c>.md</c> comes first: it's the DAT's extension.
/// </summary>
public sealed class SegaPicoHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(SegaPicoHandler), "boxart.pico.png");

    public string Id => "pico";
    public string DisplayName => "Sega Pico";
    public string ShortName => "Pico";
    public string Family => "pico";
    public IReadOnlyList<string> RomExtensions { get; } = [".md", ".bin", ".pco"];
    public long MinRomSizeBytes => 8192;
    public long MaxRomSizeBytes => 4 * 1024 * 1024;
    public string DatName => "Sega - PICO";

    /// <summary>Libretro has no releaseyear, developer, publisher or genre file for Sega - PICO.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>Sega Pico P5: PicoDrive, whose files the operator supplies for 32X too.</summary>
    public string BrowserCore => "picodrive";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => MegaDriveHeader.Read(rom, Id);
}
