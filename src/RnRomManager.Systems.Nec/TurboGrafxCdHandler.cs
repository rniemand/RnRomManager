using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nec;

/// <summary>
/// TurboGrafx-CD / PC Engine CD (TurboGrafx-CD B1). The Redump DAT lists one track per disc, usually the data track
/// (Track 02, after an audio track); a cue and its other tracks form the set, and a standalone <c>.chd</c> is a release of
/// its own. Stored outside the ZIP under exact names, as Sega CD; the Redump DAT has no metadat, and there is no header.
/// Its own family keeps it apart from TurboGrafx-16 HuCards.
/// A CloneCD <c>.ccd</c> with its <c>.img</c> and optional <c>.sub</c> is a set too (CloneCD B1), played through a cue built from the <c>.ccd</c>.
/// </summary>
public sealed class TurboGrafxCdHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => TurboGrafx16Handler.LoadResource("boxart.tgcd.png"));

    public string Id => "tgcd";
    public string DisplayName => "TurboGrafx-CD / PC Engine CD";
    public string ShortName => "TGCD";
    public string Family => "tgcd";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.chd</c> never matches the DAT.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".chd", ".ccd", ".img", ".sub"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "NEC - PC Engine CD - TurboGrafx-CD";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>TurboGrafx-CD B5: Beetle PCE, shared with TurboGrafx-16; the play URL's <c>disc</c> flag tells the two apart.</summary>
    public string? BrowserCore => "mednafen_pce";

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
