using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.WonderSwan;

/// <summary>
/// Benesse Pocket Challenge V2 carts, <c>.pc2</c> (spec P1, P2). Reads the WonderSwan footer but is its own <c>pc2</c> family,
/// so an unmatched cart is never classified or renamed as <c>.ws</c>/<c>.wsc</c>. Not a <see cref="WonderSwanHandlerBase"/>:
/// that base fixes the family.
/// </summary>
public sealed class PocketChallengeV2Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(PocketChallengeV2Handler), "boxart.pc2.png");

    public string Id => WonderSwanFooterReader.PocketChallengeV2Id;
    public string DisplayName => "Pocket Challenge V2";
    public string ShortName => "PC2";
    public string Family => WonderSwanFooterReader.PocketChallengeV2Id;

    public IReadOnlyList<string> RomExtensions { get; } = [".pc2"];

    /// <summary>The footer is 16 bytes; anything shorter can't be a cart.</summary>
    public long MinRomSizeBytes => WonderSwanFooterReader.FooterSize;

    /// <summary>16 MiB: the DAT's largest entry.</summary>
    public long MaxRomSizeBytes => 16L * 1024 * 1024;

    public string DatName => "Benesse - Pocket Challenge V2";

    /// <summary>libretro has no metadat for it.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>Beetle WonderSwan loads <c>.pc2</c> as a WonderSwan cart (spec P3).</summary>
    public string BrowserCore => "mednafen_wswan";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) =>
        WonderSwanFooterReader.Read(rom) is { } header ? header with { ClassifiedSystemId = Id } : null;
}
