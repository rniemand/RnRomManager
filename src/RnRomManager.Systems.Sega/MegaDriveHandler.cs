using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Mega Drive / Genesis (spec D1–D3). The header at <c>0x100</c> is optional: a file without <c>SEGA</c> in the
/// system-type field is not invalid, so an unmatched file goes to Review. Raw dumps are already big-endian, so hashing
/// uses the file as-is, except an interleaved <c>.smd</c> copier dump, which is deinterleaved first (so it's stored as <c>.md</c>).
/// </summary>
public sealed class MegaDriveHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(MegaDriveHandler), "boxart.megadrive.png");

    public string Id => "md";
    public string DisplayName => "Mega Drive / Genesis";
    public string ShortName => "MD";
    public string Family => "megadrive";

    /// <summary><c>[0]</c> (<c>.md</c>) is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".md", ".bin", ".gen", ".smd"];

    public long MinRomSizeBytes => 8192;
    /// <summary>16 MiB plus an <c>.smd</c> header; checked before <see cref="NormalizeForHashing"/> strips it.</summary>
    public long MaxRomSizeBytes => 16 * 1024 * 1024 + SmdHeaderBytes;
    public string DatName => "Sega - Mega Drive - Genesis";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>
    /// Genesis B5: Genesis Plus GX, whose files the operator supplies (<c>Emulator:GenesisCorePath</c>); Play shows
    /// either way and explains a missing setup.
    /// </summary>
    public string BrowserCore => "genesis_plus_gx";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    private const int SmdHeaderBytes = 512;
    private const int SmdBlockBytes = 16384;

    /// <summary>
    /// Deinterleaves a Super Magic Drive dump, detected by content (size is 512 + whole 16 KiB blocks and header bytes 8–9
    /// are <c>AA BB</c>), not extension. Each block holds the odd bytes in its first half and the even bytes in its second.
    /// </summary>
    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom)
    {
        var span = rom.Span;
        if (rom.Length % SmdBlockBytes != SmdHeaderBytes || rom.Length == SmdHeaderBytes || span[8] != 0xAA || span[9] != 0xBB)
            return rom;
        var body = span[SmdHeaderBytes..];
        var output = new byte[body.Length];
        const int half = SmdBlockBytes / 2;
        for (var block = 0; block < body.Length; block += SmdBlockBytes)
            for (var i = 0; i < half; i++)
            {
                output[block + 2 * i] = body[block + half + i];
                output[block + 2 * i + 1] = body[block + i];
            }
        return output;
    }

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => MegaDriveHeader.Read(rom, Id);
}
