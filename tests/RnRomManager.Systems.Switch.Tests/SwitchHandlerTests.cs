using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Switch.Tests;

/// <summary>Nintendo Switch spec N1 (contract), N2 (header) and N3 (box art).</summary>
public class SwitchHandlerTests
{
    private static readonly ISystemHandler Handler = new SwitchHandler();
    private const string Id = "0100F2C0115B6000";

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("switch", "Nintendo Switch", "Switch", "switch", "Nintendo - Nintendo Switch")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".nsp", ".xci", ".nsz", ".xcz" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((1024L, 68_719_476_736L)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource), Is.EqualTo((false, DatSource.TitleDb)));
    }

    private static RomHeader Read(byte[] rom) => Handler.ReadHeader(rom)!;

    private static byte[] Nsp(params string[] names) =>
        new SyntheticNspBuilder { Entries = [.. names.Select(n => (n, 16))], Seed = 1 }.Build();

    [Test]
    public void Pfs0_WithATicket_GivesItsTitleId_UpperCased()
    {
        var header = Read(Nsp("abc.nca", SyntheticNspBuilder.TicketName(Id.ToLowerInvariant()), "abc.cert"));

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("switch", (string?)null, true)));
        Assert.That(header.Fields, Is.EquivalentTo(new Dictionary<string, object?> { ["container"] = "PFS0", ["fileCount"] = 3u, ["titleId"] = Id }));
    }

    [Test]
    public void Pfs0_WithoutATicket_HasNoTitleId()
    {
        var header = Read(Nsp("a.nca", "b.cnmt.nca"));

        Assert.That((header.IsPlausible, header.Fields["fileCount"], header.Fields["titleId"]), Is.EqualTo((true, (object?)2u, (object?)null)));
    }

    [Test]
    public void FourBytePfs0_IsPlausible_WithNoCountOrId()
    {
        var header = Read("PFS0"u8.ToArray());

        Assert.That((header.IsPlausible, header.Fields["fileCount"], header.Fields["titleId"]), Is.EqualTo((true, (object?)null, (object?)null)));
    }

    [Test]
    public void EightBytePfs0_KeepsItsCount_WithNoId()
    {
        var header = Read([.. "PFS0"u8, 5, 0, 0, 0]);

        Assert.That((header.IsPlausible, header.Fields["fileCount"], header.Fields["titleId"]), Is.EqualTo((true, (object?)5u, (object?)null)));
    }

    [TestCase(0x100)]
    [TestCase(0x1100)]
    public void Head_IsAnXci(int offset)
    {
        var header = Read(new SyntheticNspBuilder { XciHeadAt = offset, Seed = 2 }.Build());

        Assert.That(header.IsPlausible, Is.True);
        Assert.That(header.Fields, Is.EquivalentTo(new Dictionary<string, object?> { ["container"] = "XCI", ["titleId"] = null }));
    }

    [Test]
    public void OnlyHeaAt0x100_IsNotPlausible()
    {
        var rom = new byte[0x103];
        "HEA"u8.CopyTo(rom.AsSpan(0x100));

        Assert.That(Read(rom).IsPlausible, Is.False);
    }

    [Test]
    public void RandomBytes_AreNotPlausible()
    {
        var rom = new byte[8192];
        new Random(3).NextBytes(rom);
        rom[0] = 0;

        Assert.That(Read(rom).IsPlausible, Is.False);
    }

    [Test]
    public void TicketNameEndingInALineFeed_GivesNoId() =>
        Assert.That(Read(Nsp(SyntheticNspBuilder.TicketName(Id) + "\n")).Fields["titleId"], Is.Null);

    [Test]
    public void TwoDifferentTicketIds_GiveNoId() =>
        Assert.That(Read(Nsp(SyntheticNspBuilder.TicketName(Id), SyntheticNspBuilder.TicketName("0100000000002000"))).Fields["titleId"], Is.Null);

    [Test]
    public void TheSameTicketIdTwice_IsFine() =>
        Assert.That(Read(Nsp(SyntheticNspBuilder.TicketName(Id), SyntheticNspBuilder.TicketName(Id.ToLowerInvariant()))).Fields["titleId"], Is.EqualTo(Id));

    /// <summary>A PFS0 whose header and string table are written by hand.</summary>
    private static byte[] Pfs0(uint count, uint tableSize, uint[] nameOffsets, byte[] table, int length = 2048)
    {
        var rom = new byte[Math.Max(length, 0x10 + nameOffsets.Length * 0x18 + table.Length)];
        "PFS0"u8.CopyTo(rom);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(8), tableSize);
        for (var i = 0; i < nameOffsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x10 + i * 0x18 + 16), nameOffsets[i]);
        table.CopyTo(rom.AsSpan(0x10 + nameOffsets.Length * 0x18));
        return rom;
    }

    private static readonly byte[] TicketTable = [.. Encoding.ASCII.GetBytes(SyntheticNspBuilder.TicketName(Id)), 0];

    [Test]
    public void HandBuiltTable_IsRead() =>
        Assert.That(Read(Pfs0(1, (uint)TicketTable.Length, [0], TicketTable)).Fields["titleId"], Is.EqualTo(Id));

    [Test]
    public void TruncatedTable_GivesNoId_ButStaysPlausible()
    {
        var rom = Pfs0(1, (uint)TicketTable.Length, [0], TicketTable)[..(0x10 + 0x18 + 10)];

        var header = Read(rom);

        Assert.That((header.IsPlausible, header.Fields["titleId"]), Is.EqualTo((true, (object?)null)));
    }

    [Test]
    public void CountOfFFFFFFFF_GivesNoId() =>
        Assert.That(Read(Pfs0(0xFFFFFFFF, (uint)TicketTable.Length, [0], TicketTable)).Fields["titleId"], Is.Null);

    [Test]
    public void NameOffsetOutsideTheTable_GivesNoId() =>
        Assert.That(Read(Pfs0(1, (uint)TicketTable.Length, [(uint)TicketTable.Length], TicketTable)).Fields["titleId"], Is.Null);

    [Test]
    public void MissingNul_GivesNoId()
    {
        var table = TicketTable[..^1]; // the name runs to the end of the declared table

        Assert.That(Read(Pfs0(1, (uint)table.Length, [0], table)).Fields["titleId"], Is.Null);
    }

    [Test]
    public void AnUnreferencedTicketLookingString_IsIgnored()
    {
        byte[] table = [.. "a.nca\0"u8, .. TicketTable];

        Assert.That(Read(Pfs0(1, (uint)table.Length, [0], table)).Fields["titleId"], Is.Null);
    }

    [Test]
    public void AReferenceIntoTheMiddleOfAString_GivesNoId()
    {
        byte[] table = [.. "xx"u8, .. TicketTable];

        Assert.That(Read(Pfs0(2, (uint)table.Length, [2, 0], table)).Fields["titleId"], Is.Null, "offset 2 follows 'x', not a NUL");
    }

    [Test]
    public void ThousandsOfEntriesAtOneUnterminatedString_AreRejected()
    {
        const int count = 40000;
        byte[] table = [.. "never ends"u8];
        var offsets = new uint[count];

        Assert.That(Read(Pfs0(count, (uint)table.Length, offsets, table, length: 0x10 + count * 0x18 + table.Length)).Fields["titleId"], Is.Null);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var info = PngInfo.Read(Handler.DefaultBoxArtPng.Span);

        Assert.That(info, Is.Not.Null);
        Assert.That((info!.Width, info.Height), Is.EqualTo((512, 512)));
    }
}
