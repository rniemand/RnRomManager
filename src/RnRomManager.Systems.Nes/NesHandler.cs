using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nes;

/// <summary>
/// Nintendo Entertainment System (spec N2). Headered <c>.nes</c> dumps, hashed as-is, header included, which is how the
/// No-Intro DAT's <c>.nes</c> entries (and the metadat CRCs) are keyed; and UNIF <c>.unf</c>/<c>.unif</c> files, which no DAT
/// lists, so they always go to Review and keep their extension.
/// </summary>
public sealed class NesHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(NesHandler), "boxart.nes.png");

    public string Id => NesHeaderReader.NesId;
    public string DisplayName => "Nintendo Entertainment System";
    public string ShortName => "NES";
    public string Family => NesHeaderReader.NesId;
    public IReadOnlyList<string> RomExtensions { get; } = [".nes", ".unf", ".unif"];

    /// <summary>An unmatched UNIF file keeps its <c>.unf</c>/<c>.unif</c>, so the stored file says what it is.</summary>
    public bool KeepsExtension(string extension) =>
        extension.Equals(".unf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".unif", StringComparison.OrdinalIgnoreCase);

    /// <summary>The header plus 4 KiB: the smallest <c>.nes</c> in the DAT.</summary>
    public long MinRomSizeBytes => NesHeaderReader.HeaderSize + 4096;

    /// <summary>Header, trainer and the largest multicart (64 MiB).</summary>
    public long MaxRomSizeBytes => NesHeaderReader.HeaderSize + 512 + 64L * 1024 * 1024;

    public string DatName => "Nintendo - Nintendo Entertainment System";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>Play B1: FCEUmm, from the stored headered <c>.nes</c>; no BIOS.</summary>
    public string BrowserCore => "fceumm";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => NesHeaderReader.Read(rom);
}
