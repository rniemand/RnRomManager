using System.Buffers.Binary;
using System.Text;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Snes.Tests;

/// <summary>SNES spec S4: candidates, priority and every header field, from hand-written headers.</summary>
public class SnesHeaderReaderTests
{
    private const int Lo = 0x7FC0, Hi = 0xFFC0, ExHi = 0x40FFC0;

    /// <summary>Writes a header at <paramref name="offset"/>; the default checksum pair is valid (1234 + EDCB).</summary>
    private static byte[] Write(byte[] rom, int offset, string title = "HAND WRITTEN", byte mapMode = 0x20, byte romType = 0,
        byte romSize = 9, byte sram = 0, byte region = 1, byte version = 0, ushort checksum = 0x1234, ushort complement = 0xEDCB)
    {
        Encoding.Latin1.GetBytes(title.PadRight(21)).CopyTo(rom, offset);
        (rom[offset + 0x15], rom[offset + 0x16], rom[offset + 0x17], rom[offset + 0x18], rom[offset + 0x19], rom[offset + 0x1B]) =
            (mapMode, romType, romSize, sram, region, version);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(offset + 0x1C), complement);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(offset + 0x1E), checksum);
        return rom;
    }

    private static object? Field(byte[] rom, string name) => SnesHeaderReader.Read(rom)!.Fields[name];

    private static Dictionary<string, object?> Invalid(int length) => new()
    {
        ["headerValid"] = false,
        ["title"] = null,
        ["mapping"] = null,
        ["fastRom"] = null,
        ["romType"] = null,
        ["romSizeKiB"] = null,
        ["sramSizeKiB"] = null,
        ["region"] = null,
        ["version"] = null,
        ["checksum"] = null,
        ["checksumValid"] = null,
        ["romSizeBytes"] = length,
    };

    [Test]
    public void Under32KiB_IsNull()
    {
        Assert.That(SnesHeaderReader.Read(new byte[0x7FFF]), Is.Null);
        Assert.That(SnesHeaderReader.Read([]), Is.Null);
    }

    [Test]
    public void ACandidatePastTheLength_IsNotRead()
    {
        // One byte short of the HiROM header: only LoROM (empty) is considered.
        var shortRom = new byte[0xFFFF];
        Encoding.ASCII.GetBytes("HIROM AREA").CopyTo(shortRom, Hi);
        Assert.That(SnesHeaderReader.Read(shortRom)!.Fields, Is.EqualTo(Invalid(0xFFFF)));

        Assert.That(Field(Write(new byte[0x10000], Hi, mapMode: 0x21), "mapping"), Is.EqualTo("HiROM"));
    }

    [TestCase(0x8000, Lo, (byte)0x20, "LoROM")]
    [TestCase(0x10000, Hi, (byte)0x21, "HiROM")]
    [TestCase(0x410000, ExHi, (byte)0x25, "ExHiROM")]
    public void EachMapping_IsFoundAtItsOffset(int size, int offset, byte mapMode, string mapping) =>
        Assert.That(Field(Write(new byte[size], offset, mapMode: mapMode), "mapping"), Is.EqualTo(mapping));

    [Test]
    public void AHandWrittenHeader_ReadsEveryField()
    {
        var rom = Write(new byte[0x8000], Lo, title: "FIELD TEST", mapMode: 0x30, romType: 0x02, romSize: 0x0A, sram: 3, region: 0,
            version: 2, checksum: 0xBEEF, complement: 0x4110);

        var header = SnesHeaderReader.Read(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("snes", "FIELD TEST", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["headerValid"] = true,
            ["title"] = "FIELD TEST",
            ["mapping"] = "LoROM",
            ["fastRom"] = true,
            ["romType"] = 2,
            ["romSizeKiB"] = 1024,
            ["sramSizeKiB"] = 8,
            ["region"] = "Japan",
            ["version"] = 2,
            ["checksum"] = "BEEF",
            ["checksumValid"] = true,
            ["romSizeBytes"] = 0x8000,
        }));
    }

    [TestCase((byte)0x20, false)]
    [TestCase((byte)0x30, true)]
    public void FastRom(byte mapMode, bool fast) => Assert.That(Field(Write(new byte[0x8000], Lo, mapMode: mapMode), "fastRom"), Is.EqualTo(fast));

    [TestCase((byte)0, null)]
    [TestCase((byte)1, 2)]
    [TestCase((byte)13, 8192)]
    [TestCase((byte)14, null)]
    [TestCase((byte)0xFF, null)]
    public void RomSizeKiB(byte value, int? kib) => Assert.That(Field(Write(new byte[0x8000], Lo, romSize: value), "romSizeKiB"), Is.EqualTo(kib));

    [TestCase((byte)0, 0)]
    [TestCase((byte)1, 2)]
    [TestCase((byte)10, 1024)]
    [TestCase((byte)11, null)]
    [TestCase((byte)0xFF, null)]
    public void SramSizeKiB(byte value, int? kib) => Assert.That(Field(Write(new byte[0x8000], Lo, sram: value), "sramSizeKiB"), Is.EqualTo(kib));

    [TestCase((byte)0, "Japan")]
    [TestCase((byte)1, "USA")]
    [TestCase((byte)2, "Europe")]
    [TestCase((byte)3, "Sweden")]
    [TestCase((byte)4, "Finland")]
    [TestCase((byte)5, "Denmark")]
    [TestCase((byte)6, "France")]
    [TestCase((byte)7, "Netherlands")]
    [TestCase((byte)8, "Spain")]
    [TestCase((byte)9, "Germany")]
    [TestCase((byte)10, "Italy")]
    [TestCase((byte)11, "China")]
    [TestCase((byte)12, "Indonesia")]
    [TestCase((byte)13, "Korea")]
    [TestCase((byte)14, null)]
    [TestCase((byte)15, "Canada")]
    [TestCase((byte)16, "Brazil")]
    [TestCase((byte)17, "Australia")]
    [TestCase((byte)18, null)]
    [TestCase((byte)0xFF, null)]
    public void Region(byte value, string? region) => Assert.That(Field(Write(new byte[0x8000], Lo, region: value), "region"), Is.EqualTo(region));

    [Test]
    public void ABadChecksumAtLoRom_LosesToAValidOneAtHiRom()
    {
        var rom = Write(new byte[0x10000], Lo, title: "LOROM", checksum: 0x1111, complement: 0x1111);
        Write(rom, Hi, title: "HIROM", mapMode: 0x21);

        Assert.That((Field(rom, "mapping"), Field(rom, "title")), Is.EqualTo(("HiROM", "HIROM")));
    }

    [Test]
    public void NoValidChecksum_PrintableTitleAndFittingMapMode_IsPlausible()
    {
        var rom = Write(new byte[0x10000], Hi, mapMode: 0x21, checksum: 0, complement: 0);

        var header = SnesHeaderReader.Read(rom)!;

        Assert.That((header.IsPlausible, header.Fields["mapping"], header.Fields["checksumValid"], header.Fields["checksum"]),
            Is.EqualTo((true, "HiROM", false, "0000")));
    }

    [TestCase("BAD\u0001TITLE", (byte)0x20, TestName = "A non-printable title byte")]
    [TestCase("", (byte)0x20, TestName = "An all-space title")]
    [TestCase("GOOD TITLE", (byte)0x21, TestName = "A HiROM map mode at the LoROM offset")]
    [TestCase("GOOD TITLE", (byte)0x00, TestName = "A map mode without 0x20")]
    public void NoValidChecksum_AndNoTitleWithFittingMapMode_IsInvalid(string title, byte mapMode)
    {
        var rom = Write(new byte[0x8000], Lo, title: title, mapMode: mapMode, checksum: 0, complement: 0);

        var header = SnesHeaderReader.Read(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("snes", (string?)null, false)));
        Assert.That(header.Fields, Is.EqualTo(Invalid(0x8000)));
    }

    [Test]
    public void AChecksumChosenTitle_FollowsTheGbRule()
    {
        // Half-width katakana become '?', the title ends at the first NUL.
        var rom = Write(new byte[0x8000], Lo, title: "±Â KANAß\0\0IGNORED", mapMode: 0x00);

        Assert.That(SnesHeaderReader.Read(rom)!.Title, Is.EqualTo("?? KANA?"));
    }

    [Test]
    public void AnExHiRomRom_WithAValidLookingHeaderAt0xFFC0_ReadsAsExHiRom()
    {
        var rom = new SyntheticSnesRomBuilder { Seed = 2, SizeKiB = 8192, Mapping = SnesMapping.ExHiROM, Title = "EXHIROM" }.Build();
        Write(rom, Hi, title: "FAKE HIROM", mapMode: 0x21);
        Write(rom, Lo, title: "FAKE LOROM");

        Assert.That((Field(rom, "mapping"), Field(rom, "title"), Field(rom, "checksumValid")), Is.EqualTo(("ExHiROM", "EXHIROM", true)));
    }

    [TestCase(0x8000)]
    [TestCase(0x10000)]
    [TestCase(0x40FFFF)]
    [TestCase(0x410000)]
    public void RandomGarbage_NeverThrows(int size)
    {
        var rom = new byte[size];
        for (var seed = 0; seed < 20; seed++)
        {
            new Random(seed).NextBytes(rom);
            Assert.DoesNotThrow(() => SnesHeaderReader.Read(rom));
        }
    }
}
