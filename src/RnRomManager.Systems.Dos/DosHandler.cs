using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Dos;

/// <summary>
/// MS-DOS (MS-DOS S1): one ZIP per game (eXoDOS / Total DOS Collection style), stored next to the game ZIP. There is no
/// DAT (<see cref="DatSource.None"/>), so every file goes to Review; there is no header.
/// </summary>
public sealed class DosHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.dos.png"));

    public string Id => "dos";
    public string DisplayName => "MS-DOS";
    public string ShortName => "DOS";
    public string Family => "dos";

    /// <summary>A <c>.zip</c> is the game itself here, not an archive to unpack (I1).</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".dosz", ".zip"];

    public long MinRomSizeBytes => 64;

    public long MaxRomSizeBytes => 4L * 1024 * 1024 * 1024;

    public string DatName => "DOS";

    /// <inheritdoc />
    public DatSource DatSource => DatSource.None;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <summary>
    /// S2: always <c>.dosz</c>, never <c>.zip</c>: Integrity and DbSync read every <c>.zip</c> under <c>roms/&lt;id&gt;/</c> as a game ZIP.
    /// DOSBox Pure mounts a <c>.dosz</c> as it does a ZIP.
    /// </summary>
    public string? SourceExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".zip" or ".dosz" ? ".dosz" : null;

    /// <summary>
    /// MS-DOS P1: DOSBox Pure, vendored. It requires threads, so it plays on the isolated <c>play-dos.html</c> page; the
    /// whole ZIP is mounted, not unpacked.
    /// </summary>
    public string? BrowserCore => "dosbox_pure";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
