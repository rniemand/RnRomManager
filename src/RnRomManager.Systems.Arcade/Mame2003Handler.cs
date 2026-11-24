using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Arcade;

/// <summary>
/// Arcade (MAME 2003-Plus) S1: one ZIP per romset, matched by set name and ROM CRCs against the MAME 2003-Plus XML DAT
/// (<see cref="DatSource.Mame"/>) and stored next to the game ZIP unchanged. There is no header to read.
/// </summary>
public sealed class Mame2003Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.mame2003.png"));

    public string Id => "mame2003";
    public string DisplayName => "Arcade (MAME 2003-Plus)";
    public string ShortName => "MAME";
    public string Family => "mame2003";

    /// <summary>A <c>.zip</c> is the romset itself here, not an archive to unpack (I1).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".mamez", ".zip"];

    /// <summary>An empty ZIP's end-of-central-directory record.</summary>
    public long MinRomSizeBytes => 22;

    public long MaxRomSizeBytes => 1L * 1024 * 1024 * 1024;

    /// <summary>Also the libretro-thumbnails folder, keyed by MAME description (G6).</summary>
    public string DatName => DatStore.MameName;

    /// <inheritdoc />
    public DatSource DatSource => DatSource.Mame;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <summary>
    /// S2: always <c>.mamez</c>, never <c>.zip</c>, as MS-DOS stores <c>.dosz</c>: Integrity and DbSync read every <c>.zip</c>
    /// under <c>roms/&lt;id&gt;/</c> as a game ZIP. A <c>.mamez</c> is the set ZIP, unchanged.
    /// </summary>
    public string? SourceExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".zip" or ".mamez" ? ".mamez" : null;

    /// <summary>
    /// P1: MAME 2003-Plus, whose files the operator supplies (<c>Emulator:Mame2003PlusCorePath</c>); it plays in the modal
    /// (no threads), with the parent and BIOS sets loaded beside the game (P3).
    /// </summary>
    public string? BrowserCore => "mame2003_plus";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
