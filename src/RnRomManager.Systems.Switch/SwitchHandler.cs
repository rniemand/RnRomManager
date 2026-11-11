using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Switch;

/// <summary>
/// Nintendo Switch (spec N1, N2): dumps are stored next to the game ZIP (<see cref="EmbedRomsInZip"/> false) and
/// identified through titledb by title id (T4), taken from a PFS0 ticket name when the header has one.
/// </summary>
public sealed class SwitchHandler : ISystemHandler
{
    public const string SystemId = "switch";

    /// <summary>A ticket name: 32 hex digits and <c>.tik</c>, the first 16 being the content's title id.</summary>
    private const int TicketNameLength = 36;

    private static ReadOnlySpan<byte> Pfs0Magic => "PFS0"u8;
    private static ReadOnlySpan<byte> XciMagic => "HEAD"u8;
    private static ReadOnlySpan<byte> TicketSuffix => ".tik"u8;

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.switch.png"));

    public string Id => SystemId;
    public string DisplayName => "Nintendo Switch";
    public string ShortName => "Switch";
    public string Family => SystemId;

    /// <summary><c>.nsz</c>/<c>.xcz</c> are compressed but keep the PFS0/XCI outer container.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".nsp", ".xci", ".nsz", ".xcz"];

    public long MinRomSizeBytes => 1024;
    public long MaxRomSizeBytes => 64L * 1024 * 1024 * 1024;
    public string DatName => "Nintendo - Nintendo Switch";

    /// <inheritdoc />
    public bool EmbedRomsInZip => false;

    public DatSource DatSource => DatSource.TitleDb;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>
    /// N2 on the first MiB: <c>PFS0</c> at 0 (NSP/NSZ) with its file count and a ticket's title id, or <c>HEAD</c> at
    /// 0x100 or 0x1100 (XCI/XCZ, the latter a full card image with its key area). Anything else is not plausible.
    /// </summary>
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom)
    {
        if (rom.StartsWith(Pfs0Magic))
        {
            uint? count = rom.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(rom[4..]) : null;
            return Header(new() { ["container"] = "PFS0", ["fileCount"] = count, ["titleId"] = TicketTitleId(rom) });
        }
        if (HasXciMagicAt(rom, 0x100) || HasXciMagicAt(rom, 0x1100))
            return Header(new() { ["container"] = "XCI", ["titleId"] = null });
        return new RomHeader(SystemId, null, IsPlausible: false, new Dictionary<string, object?>());
    }

    private static RomHeader Header(Dictionary<string, object?> fields) => new(SystemId, null, IsPlausible: true, fields);

    /// <summary>All four magic bytes must be inside the span.</summary>
    private static bool HasXciMagicAt(ReadOnlySpan<byte> rom, int offset) =>
        rom.Length >= offset + XciMagic.Length && rom.Slice(offset, XciMagic.Length).SequenceEqual(XciMagic);

    /// <summary>
    /// The upper-cased title id of the PFS0's ticket names, when they agree on one. Entries are 0x18 bytes from 0x10
    /// (u64 offset, u64 size, u32 name offset, u32 reserved); the string table (size: u32 at 8) follows them. A
    /// truncated or out-of-range table or name, an unterminated name, a reference into the middle of a string, or two
    /// different ticket ids give null, so T4 falls back to the file name.
    /// </summary>
    private static string? TicketTitleId(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 16)
            return null;
        long count = BinaryPrimitives.ReadUInt32LittleEndian(rom[4..]);
        long tableSize = BinaryPrimitives.ReadUInt32LittleEndian(rom[8..]);
        var entriesEnd = 0x10 + count * 0x18;
        var tableEnd = entriesEnd + tableSize;
        if (tableEnd > rom.Length)
            return null;
        var table = rom[(int)entriesEnd..(int)tableEnd];

        // One reverse scan: where the string starting at each byte ends, or -1 when it never does.
        var nextNul = new int[table.Length];
        var next = -1;
        for (var i = table.Length - 1; i >= 0; i--)
        {
            if (table[i] == 0)
                next = i;
            nextNul[i] = next;
        }

        string? found = null;
        for (var entry = 0; entry < count; entry++)
        {
            long nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(rom[(0x10 + entry * 0x18 + 16)..]);
            if (nameOffset >= table.Length || (nameOffset > 0 && table[(int)nameOffset - 1] != 0) || nextNul[(int)nameOffset] < 0)
                return null;
            if (nextNul[(int)nameOffset] - nameOffset != TicketNameLength)
                continue;
            var name = table.Slice((int)nameOffset, TicketNameLength);
            if (!name[32..].SequenceEqual(TicketSuffix) || !IsHex(name[..32]))
                continue;
            var id = System.Text.Encoding.ASCII.GetString(name[..16]).ToUpperInvariant();
            if (found is not null && found != id)
                return null;
            found = id;
        }
        return found;
    }

    private static bool IsHex(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (!char.IsAsciiHexDigit((char)b))
                return false;
        }
        return true;
    }

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(SwitchHandler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Switch.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
