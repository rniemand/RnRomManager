using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore Amiga. Matched against libretro's TOSEC DAT (<c>metadat/tosec/Commodore - Amiga.dat</c>: about 40,000
/// <c>.adf</c> floppy images of 462 KiB–2.2 MiB); its No-Intro DAT lists only <c>.ipf</c>, which the browser core can't
/// read (PUAE loads IPF through capsimg, a native library the WebAssembly build doesn't have), so only ADF is taken.
/// ADF has no header (a custom boot block is common), so <see cref="ReadHeader"/> always returns null and an unmatched
/// file goes to Review. Each disk of a multi-disk game is its own release.
/// </summary>
public sealed class AmigaHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.amiga.png"));

    public string Id => "amiga";
    public string DisplayName => "Commodore Amiga";
    public string ShortName => "Amiga";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".adf"];
    public long MinRomSizeBytes => 256 * 1024;
    public long MaxRomSizeBytes => 4 * 1024 * 1024;
    public string DatName => "Commodore - Amiga";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string DatFolder => DatKind.Tosec;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <summary>PUAE, vendored; without a Kickstart ROM it boots its built-in AROS replacement.</summary>
    public string BrowserCore => "puae";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
