using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nec;

/// <summary>
/// TurboGrafx-16 / PC Engine HuCards (spec T1, T2). HuCards have no header, so <see cref="ReadHeader"/> is always null and an
/// unmatched file goes to Review. Sizes follow the libretro <c>NEC - PC Engine - TurboGrafx 16</c> DAT (72 KiB–2.5 MiB).
/// </summary>
public sealed class TurboGrafx16Handler : ISystemHandler
{
    private const int CopierHeaderBytes = 512;

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.tg16.png"));

    public string Id => "tg16";
    public string DisplayName => "TurboGrafx-16";
    public string ShortName => "TG16";
    public string Family => "tg16";

    public IReadOnlyList<string> RomExtensions { get; } = [".pce"];

    public long MinRomSizeBytes => 73728;

    /// <summary>The largest DAT entry plus a copier header: checked before <see cref="NormalizeForHashing"/> strips it.</summary>
    public long MaxRomSizeBytes => 2621440 + CopierHeaderBytes;

    public string DatName => "NEC - PC Engine - TurboGrafx 16";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;

    /// <summary>
    /// Drops a 512-byte copier header, detected by size alone (HuCard dumps are multiples of 8 KiB). Three aftermarket protos
    /// in the DAT have that size unheadered; they get stripped, don't match and go to Review (spec T2, accepted).
    /// </summary>
    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) =>
        rom.Length % 8192 == CopierHeaderBytes ? rom[CopierHeaderBytes..] : rom;

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(TurboGrafx16Handler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Nec.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
