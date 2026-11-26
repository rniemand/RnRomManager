using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nes;

/// <summary>
/// Famicom Disk System. Matched against libretro's No-Intro DAT, whose <c>.fds</c> entries are headerless (65,500 bytes
/// per disk side), so a dump's 16-byte fwNES header (<c>FDS\x1A</c>) is stripped before hashing and storing, as a SNES
/// copier header is. The DAT's QuickDisk <c>.qd</c> images aren't taken: FCEUmm doesn't read them. Its own family keeps
/// an <c>.fds</c> out of the NES DAT.
/// </summary>
public sealed class FdsHandler : ISystemHandler
{
    public const int SideBytes = 65_500;
    private const int FwNesHeaderSize = 16;

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(FdsHandler), "boxart.fds.png");

    public string Id => "fds";
    public string DisplayName => "Famicom Disk System";
    public string ShortName => "FDS";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".fds"];

    /// <summary>One headerless disk side.</summary>
    public long MinRomSizeBytes => SideBytes;

    /// <summary>FCEUmm's limit of eight sides, plus the fwNES header.</summary>
    public long MaxRomSizeBytes => FwNesHeaderSize + 8L * SideBytes;

    public string DatName => "Nintendo - Family Computer Disk System";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>FCEUmm, which detects a disk image by its content; it needs <c>disksys.rom</c> from <c>Paths:Bios</c>.</summary>
    public string BrowserCore => "fceumm";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>
    /// Called after <see cref="NormalizeForHashing"/>, so side 1 starts with its disk info block
    /// (<c>\x01*NINTENDO-HVC*</c>); anything else is implausible, so an unmatched file fails <c>InvalidHeader</c>.
    /// </summary>
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x17)
            return null;
        if (rom[0] != 0x01 || !rom.Slice(1, 14).SequenceEqual("*NINTENDO-HVC*"u8))
            return new RomHeader(Id, null, false, new Dictionary<string, object?> { ["headerValid"] = false });
        return new RomHeader(Id, null, true, new Dictionary<string, object?>
        {
            ["headerValid"] = true,
            ["manufacturerCode"] = rom[0x0F],
            ["gameCode"] = Encoding.ASCII.GetString(rom.Slice(0x10, 3)),
            ["revision"] = rom[0x14],
            ["sides"] = rom.Length / SideBytes,
        });
    }

    /// <summary>Strips a leading fwNES header (<c>FDS\x1A</c>); a headerless image is kept as it is.</summary>
    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) =>
        rom.Length > FwNesHeaderSize && rom.Span[..4].SequenceEqual("FDS\x1A"u8) ? rom[FwNesHeaderSize..] : rom;
}
