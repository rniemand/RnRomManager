using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Arcade;

/// <summary>
/// Arcade (FinalBurn Neo) S1: one <c>.zip</c> or <c>.7z</c> per romset, matched by set name and ROM CRCs against the FBNeo
/// arcade XML DAT (<see cref="DatSource.Fbneo"/>) and stored next to the game ZIP unchanged. There is no header to read.
/// </summary>
public sealed class FbneoHandler : ISystemHandler
{
    /// <summary>S1: the FBNeo store's <c>DatName</c>; also the libretro-thumbnails folder (G6).</summary>
    public const string FbneoDatName = "FBNeo - Arcade Games";

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.fbneo.png"));

    public string Id => "fbneo";
    public string DisplayName => "Arcade (FinalBurn Neo)";
    public string ShortName => "FBNeo";
    public string Family => "fbneo";

    /// <summary>A <c>.zip</c> or <c>.7z</c> is the romset itself here, not an archive to unpack (I1).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".fbneoz", ".fbneo7z", ".zip", ".7z"];

    /// <summary>An empty ZIP's end-of-central-directory record.</summary>
    public long MinRomSizeBytes => 22;

    public long MaxRomSizeBytes => 1L * 1024 * 1024 * 1024;

    public string DatName => FbneoDatName;

    /// <inheritdoc />
    public DatSource DatSource => DatSource.Fbneo;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <summary>
    /// S2: <c>.fbneoz</c> or <c>.fbneo7z</c>, never <c>.zip</c>, as MAME stores <c>.mamez</c>: Integrity and DbSync read every
    /// <c>.zip</c> under <c>roms/&lt;id&gt;/</c> as a game ZIP. The bytes are the set archive's, unchanged.
    /// </summary>
    public string? SourceExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".zip" or ".fbneoz" => ".fbneoz",
        ".7z" or ".fbneo7z" => ".fbneo7z",
        _ => null,
    };

    /// <summary>
    /// P1: FinalBurn Neo, whose files the operator supplies (<c>Emulator:FbneoCorePath</c>); it plays in the modal from
    /// <c>/download</c>, with its parent sets and board BIOS sets from <c>/romset</c>.
    /// </summary>
    public string? BrowserCore => "fbneo";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
