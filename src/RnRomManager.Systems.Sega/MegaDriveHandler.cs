using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega Mega Drive / Genesis (spec D1–D3). The header at <c>0x100</c> is optional: a file without <c>SEGA</c> in the
/// system-type field is not invalid, so an unmatched file goes to Review. Raw dumps are already big-endian, so hashing
/// uses the file as-is; interleaved <c>.smd</c> dumps aren't supported.
/// </summary>
public sealed class MegaDriveHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.megadrive.png"));

    public string Id => "md";
    public string DisplayName => "Mega Drive / Genesis";
    public string ShortName => "MD";
    public string Family => "megadrive";

    /// <summary><c>[0]</c> (<c>.md</c>) is what a file resolved through Review without a DAT entry is stored as.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".md", ".bin", ".gen"];

    public long MinRomSizeBytes => 8192;
    public long MaxRomSizeBytes => 16 * 1024 * 1024;
    public string DatName => "Sega - Mega Drive - Genesis";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x200)
            return null;
        var systemType = Text(rom.Slice(0x100, 16));
        if (systemType is null || !systemType.StartsWith("SEGA", StringComparison.Ordinal))
            return null;
        var domestic = Text(rom.Slice(0x120, 48));
        var overseas = Text(rom.Slice(0x150, 48));
        return new RomHeader(Id, overseas ?? domestic, true, new Dictionary<string, object?>
        {
            ["systemType"] = systemType,
            ["copyright"] = Text(rom.Slice(0x110, 16)),
            ["domesticTitle"] = domestic,
            ["overseasTitle"] = overseas,
            ["serial"] = Text(rom.Slice(0x180, 14)),
            ["checksum"] = $"{rom[0x18E]:X2}{rom[0x18F]:X2}",
            ["region"] = Text(rom.Slice(0x1F0, 3)),
        });
    }

    /// <summary>ASCII with every byte outside <c>0x20–0x7E</c> as a space, runs of spaces collapsed, trimmed; empty → null.</summary>
    private static string? Text(ReadOnlySpan<byte> field)
    {
        var sb = new StringBuilder(field.Length);
        foreach (var b in field)
        {
            var c = b is >= 0x20 and <= 0x7E ? (char)b : ' ';
            if (c != ' ' || (sb.Length > 0 && sb[^1] != ' '))
                sb.Append(c);
        }
        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }
}
