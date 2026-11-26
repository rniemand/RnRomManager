using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NeoGeoPocket;

/// <summary>Shared by the NGP and NGPC handlers: one family, one header format, same limits.</summary>
public abstract class NeoGeoPocketHandlerBase : ISystemHandler
{
    public const string FamilyId = "neogeopocket";

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt;

    protected NeoGeoPocketHandlerBase() => _boxArt = BoxArt.Lazy(typeof(NeoGeoPocketHandlerBase), $"boxart.{Id}.png");

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ShortName { get; }
    public string Family => FamilyId;
    public abstract IReadOnlyList<string> RomExtensions { get; }

    /// <summary>The header is 64 bytes; anything shorter can't be a cart.</summary>
    public long MinRomSizeBytes => NeoGeoPocketHeaderReader.HeaderEnd;

    /// <summary>4 MiB: the DATs' largest entry and the 32 Mbit cartridge maximum.</summary>
    public long MaxRomSizeBytes => 4L * 1024 * 1024;

    public abstract string DatName { get; }

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <inheritdoc />
    public string? BrowserCore => "mednafen_ngp";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => NeoGeoPocketHeaderReader.Read(rom);
}
