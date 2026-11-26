using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.Systems.NintendoDs.Tests;

public class Nintendo3DsHeaderReaderTests
{
    private const int Partition = 0x4000;

    /// <summary>An NCSD header with partition 0 at 0x4000 holding an NCCH header, in a 0x4200-byte buffer.</summary>
    private static byte[] Ncsd(
        int length = Partition + 0x200,
        uint partitionUnits = Partition / 0x200,
        bool ncch = true,
        byte[]? productCode = null)
    {
        var rom = new byte[length];
        "NCSD"u8.CopyTo(rom.AsSpan(0x100));
        BinaryPrimitives.WriteUInt64LittleEndian(rom.AsSpan(0x108), 0x0004000000ABCD00);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x120), partitionUnits);
        if (ncch && rom.Length >= Partition + 0x200)
        {
            "NCCH"u8.CopyTo(rom.AsSpan(Partition + 0x100));
            "01"u8.CopyTo(rom.AsSpan(Partition + 0x110));
            BinaryPrimitives.WriteUInt64LittleEndian(rom.AsSpan(Partition + 0x118), 0x0004000000ABCD00);
            (productCode ?? "CTR-P-ABCE"u8.ToArray()).CopyTo(rom.AsSpan(Partition + 0x150));
        }
        return rom;
    }

    [Test]
    public void ShorterThan0x200_IsNull() =>
        Assert.That(Nintendo3DsHeaderReader.Read(Ncsd().AsSpan(0, 0x1FF)), Is.Null);

    [Test]
    public void Cia_IsNull()
    {
        var rom = new byte[0x4000];
        BinaryPrimitives.WriteUInt32LittleEndian(rom, 0x2020);
        Assert.That(Nintendo3DsHeaderReader.Read(rom), Is.Null);
    }

    [Test]
    public void NcsdWithNcch_ReadsEveryField()
    {
        var header = Nintendo3DsHeaderReader.Read(Ncsd())!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("3ds", (string?)null, true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["format"] = "NCSD",
            ["mediaId"] = "0004000000ABCD00",
            ["productCode"] = "CTR-P-ABCE",
            ["makerCode"] = "01",
            ["programId"] = "0004000000ABCD00",
            ["encrypted"] = true,
        }));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[] { "format", "mediaId", "productCode", "makerCode", "programId", "encrypted" }));
    }

    [Test]
    public void NoCryptoFlag_IsNotEncrypted()
    {
        var rom = Ncsd();
        rom[Partition + 0x18F] = 0x04;
        Assert.That(Nintendo3DsHeaderReader.Read(rom)!.Fields["encrypted"], Is.EqualTo(false));
    }

    [Test]
    public void PartitionPastTheSpan_LeavesNcchFieldsNull()
    {
        var header = Nintendo3DsHeaderReader.Read(Ncsd(length: 0x4100))!;
        AssertNoNcch(header);
    }

    [Test]
    public void HugePartitionOffset_DoesNotThrow()
    {
        var header = Nintendo3DsHeaderReader.Read(Ncsd(partitionUnits: 0xFFFFFFFF))!;
        AssertNoNcch(header);
    }

    [Test]
    public void PartitionWithoutNcch_LeavesNcchFieldsNull()
    {
        var header = Nintendo3DsHeaderReader.Read(Ncsd(ncch: false))!;
        AssertNoNcch(header);
    }

    [TestCase(new byte[] { 0x41, 0x42, 0x43, 0x44, 0, 0, 0x45 }, "ABCD")]
    [TestCase(new byte[] { 0x41, 0x20, 0x20 }, "A")]
    [TestCase(new byte[] { 0, 0, 0 }, null)]
    [TestCase(new byte[] { 0x41, 0x7F, 0xE9, 0x01 }, "A???")]
    public void ProductCode(byte[] stored, string? expected)
    {
        var code = new byte[16];
        stored.CopyTo(code, 0);
        Assert.That(Nintendo3DsHeaderReader.Read(Ncsd(productCode: code))!.Fields["productCode"], Is.EqualTo(expected));
    }

    [Test]
    public void MakerCode_IsKeptWhole_WithNonPrintableBytesReplaced()
    {
        var rom = Ncsd();
        rom[Partition + 0x110] = 0x20;
        rom[Partition + 0x111] = 0x00;
        Assert.That(Nintendo3DsHeaderReader.Read(rom)!.Fields["makerCode"], Is.EqualTo(" ?"));
    }

    [Test]
    public void NoMagic_IsImplausible_WithANullFormat()
    {
        var rom = new byte[0x4000];
        Encoding.ASCII.GetBytes("junk").CopyTo(rom, 0x100);

        var header = Nintendo3DsHeaderReader.Read(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("3ds", (string?)null, false)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?> { ["format"] = null }));
    }

    private static void AssertNoNcch(Core.Systems.RomHeader header)
    {
        Assert.That(header.IsPlausible, Is.True);
        Assert.That((header.Fields["format"], header.Fields["mediaId"]), Is.EqualTo(("NCSD", "0004000000ABCD00")));
        Assert.That(new[] { header.Fields["productCode"], header.Fields["makerCode"], header.Fields["programId"], header.Fields["encrypted"] }, Is.All.Null);
        Assert.That(header.Fields, Has.Count.EqualTo(6));
    }
}
