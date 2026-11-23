using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Saturn. The redump DAT lists each disc's data track as <c>.bin</c>; a cue and its other tracks form the set, and a <c>.nrg</c> image always goes to Review.
/// Stored outside the ZIP under exact names (disc systems B4, B5); the redump DAT has no metadat, and there is no header.
/// A CloneCD <c>.ccd</c> with its <c>.img</c> and optional <c>.sub</c> is a set too (CloneCD B1), played through a cue built from the <c>.ccd</c>.
/// </summary>
public sealed class SaturnHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.saturn.png"));

    public string Id => "saturn";
    public string DisplayName => "Saturn";
    public string ShortName => "Saturn";
    public string Family => "saturn";

    /// <summary><c>[0]</c> is what a file resolved through Review without a DAT entry is stored as. A <c>.chd</c> is a lone file that never matches the DAT (CHD B1–B2); an <c>.iso</c> is a lone image like Sega CD's.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".bin", ".cue", ".nrg", ".chd", ".iso", ".ccd", ".img", ".sub"];

    public long MinRomSizeBytes => 16;

    public long MaxRomSizeBytes => 1024L * 1024 * 1024;

    public string DatName => "Sega - Saturn";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    /// <inheritdoc />
    public bool FileSets => true;

    /// <inheritdoc />
    public string DatFolder => DatKind.Redump;

    /// <inheritdoc />
    public IReadOnlyList<string> MetadatKinds => [];

    /// <summary>
    /// Saturn B6: Yabause plays a bin/cue disc from play-disc, one per launch; an <c>.nrg</c> release never plays.
    /// </summary>
    public string BrowserCore => "yabause";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
