using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore Amiga. Matched against libretro's TOSEC DAT (<c>metadat/tosec/Commodore - Amiga.dat</c>: about 40,000
/// <c>.adf</c> floppy images of 462 KiB–2.2 MiB, and about 3,800 <c>.ipf</c> images of 66 KiB–1.9 MiB). An <c>.adz</c> (gzipped
/// ADF) is inflated to the ADF before hashing. IPF is kept for the library only: PUAE reads it through capsimg, a native
/// library the WebAssembly build doesn't have, so an <c>.ipf</c> never plays (<c>canPlay</c>, the games list's <c>playRomId</c>).
/// ADF has no header (a custom boot block is common), so <see cref="ReadHeader"/> always returns null and an unmatched
/// file goes to Review. Each disk of a multi-disk game is its own release. Hard-disk images (<c>.hdf</c>, zipped
/// <c>.hdz</c>), WHDLoad <c>.lha</c> installs and the other PUAE floppy formats (<c>.dms</c>, <c>.fdi</c>, <c>.raw</c>)
/// are never in the TOSEC DAT, so they always go to Review; hard-disk play needs <c>kick40068.A1200</c>.
/// </summary>
public sealed class AmigaHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(AmigaHandler), "boxart.amiga.png");

    public string Id => "amiga";
    public string DisplayName => "Commodore Amiga";
    public string ShortName => "Amiga";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".adf", ".adz", ".ipf", ".hdf", ".hdz", ".lha", ".dms", ".fdi", ".raw"];

    /// <summary>Review keeps every extension (<c>.ipf</c>, <c>.hdf</c>, …) but an <c>.adf</c> or (inflated) <c>.adz</c>, which becomes <c>.adf</c>.</summary>
    public bool KeepsExtension(string extension) =>
        !extension.Equals(".adf", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".adz", StringComparison.OrdinalIgnoreCase);

    /// <summary>1 KiB, since an <c>.adz</c> of a mostly empty disk is tiny; the TOSEC ADFs themselves are 462 KiB–2.2 MiB.</summary>
    public long MinRomSizeBytes => 1024;

    /// <summary>64 MiB: an embedded ROM is read into one array at ingest, so this is the in-ZIP ceiling for an HDF (as N64).</summary>
    public long MaxRomSizeBytes => 64L * 1024 * 1024;
    public string DatName => "Commodore - Amiga";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string DatFolder => DatKind.Tosec;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <summary>
    /// PUAE, vendored; it uses <c>kick34005.A500</c>/<c>kick40068.A1200</c> from the BIOS folder when present and its built-in
    /// AROS replacement otherwise.
    /// </summary>
    public string BrowserCore => "puae";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;

    /// <summary>
    /// A gzip stream (an <c>.adz</c>, recognised by its magic) is inflated to its ADF, which is hashed against TOSEC and
    /// stored. A corrupt one, or one inflating past <see cref="MaxRomSizeBytes"/>, is kept as it is: it matches nothing
    /// and goes to Review (PUAE still recognises gzip by its magic when it's played).
    /// </summary>
    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => GzipImage.Inflate(rom, MaxRomSizeBytes);
}
