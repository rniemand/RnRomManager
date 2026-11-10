using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Coleco;

/// <summary>
/// ColecoVision (spec C2, C3). Cartridges start with the BIOS magic <c>AA 55</c> (title screen) or <c>55 AA</c> (skip it);
/// anything else is not plausible, so an unmatched file goes to <c>InvalidHeader</c>. Extensions and sizes follow the
/// libretro <c>Coleco - ColecoVision</c> DAT (8–32 KiB, <c>.col</c>).
/// </summary>
public sealed class ColecoVisionHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.coleco.png"));

    public string Id => "coleco";
    public string DisplayName => "ColecoVision";
    public string ShortName => "CV";
    public string Family => "coleco";

    /// <summary><c>[0]</c> (<c>.col</c>) is what a file resolved through Review is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".col", ".rom", ".bin"];

    public long MinRomSizeBytes => 8192;

    public long MaxRomSizeBytes => 32768;

    public string DatName => "Coleco - ColecoVision";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        var magic = Convert.ToHexString(rom[..Math.Min(2, rom.Length)]);
        return new RomHeader(Id, null, magic is "AA55" or "55AA", new Dictionary<string, object?> { ["magic"] = magic });
    }

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(ColecoVisionHandler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Coleco.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
