using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.WonderSwan;

/// <summary>Shared by the WS and WSC handlers: one family, one footer format, same limits.</summary>
public abstract class WonderSwanHandlerBase : ISystemHandler
{
    public const string FamilyId = "wonderswan";

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt;

    protected WonderSwanHandlerBase() => _boxArt = new(() => EmbeddedResources.Load($"boxart.{Id}.png"));

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ShortName { get; }
    public string Family => FamilyId;
    public abstract IReadOnlyList<string> RomExtensions { get; }

    /// <summary>The footer is 16 bytes; anything shorter can't be a cart.</summary>
    public long MinRomSizeBytes => WonderSwanFooterReader.FooterSize;

    /// <summary>16 MiB: the DATs' largest entry; shared so a mono cart named <c>.wsc</c> isn't TooLarge.</summary>
    public long MaxRomSizeBytes => 16L * 1024 * 1024;

    public abstract string DatName { get; }

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <inheritdoc />
    public string? BrowserCore => "mednafen_wswan";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => WonderSwanFooterReader.Read(rom);
}
