using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nec;

/// <summary>
/// PC Engine SuperGrafx HuCards (spec G1, G2). Headerless like TurboGrafx-16, so <see cref="ReadHeader"/> is always null and
/// an unmatched file goes to Review. Sizes follow the libretro <c>NEC - PC Engine SuperGrafx</c> DAT (512 KiB–1 MiB).
/// </summary>
public sealed class SuperGrafxHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(SuperGrafxHandler), "boxart.sgx.png");

    public string Id => "sgx";
    public string DisplayName => "PC Engine SuperGrafx";
    public string ShortName => "SGX";
    public string Family => "sgx";

    public IReadOnlyList<string> RomExtensions { get; } = [".sgx"];

    public long MinRomSizeBytes => 524288;

    /// <summary>The largest DAT entry plus a copier header: checked before <see cref="NormalizeForHashing"/> strips it.</summary>
    public long MaxRomSizeBytes => 1048576 + 512;

    public string DatName => "NEC - PC Engine SuperGrafx";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>Beetle PCE: the <c>.sgx</c> extension (case-sensitive) switches it to SuperGrafx mode (spec G3).</summary>
    public string BrowserCore => "mednafen_pce";

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;

    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => TurboGrafx16Handler.StripCopierHeader(rom);
}
