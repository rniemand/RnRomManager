namespace RnRomManager.Systems.Nes.Tests;

/// <summary>N4: every header field for iNES and NES 2.0, from hand-written headers.</summary>
public class NesHeaderReaderTests
{
    /// <summary>A 16-byte header with the magic and the given bytes 4–15, followed by <paramref name="body"/> zero bytes.</summary>
    private static byte[] Header(byte b4 = 2, byte b5 = 1, byte b6 = 0, byte b7 = 0, byte b8 = 0, byte b9 = 0, byte b12 = 0, int body = 0)
    {
        var rom = new byte[16 + body];
        "NES\x1A"u8.CopyTo(rom);
        (rom[4], rom[5], rom[6], rom[7], rom[8], rom[9], rom[12]) = (b4, b5, b6, b7, b8, b9, b12);
        return rom;
    }

    private static object? Field(byte[] rom, string name) => NesHeaderReader.Read(rom)!.Fields[name];

    [Test]
    public void Under16Bytes_IsNull()
    {
        Assert.That(NesHeaderReader.Read(new byte[15]), Is.Null);
        Assert.That(NesHeaderReader.Read([]), Is.Null);
    }

    [Test]
    public void AHandWrittenINesHeader_ReadsEveryField()
    {
        // Mapper 1 (MMC1), 128 KiB PRG, 64 KiB CHR, vertical, battery, NTSC: "NES" 1A 08 08 13 00 00 00 00 …
        byte[] rom = [0x4E, 0x45, 0x53, 0x1A, 0x08, 0x08, 0x13, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0xAA, 0xBB];

        var header = NesHeaderReader.Read(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("nes", (string?)null, true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["headerValid"] = true,
            ["format"] = "iNES",
            ["mapper"] = 1,
            ["prgRomSizeBytes"] = 131072,
            ["chrRomSizeBytes"] = 65536,
            ["mirroring"] = "Vertical",
            ["battery"] = true,
            ["trainer"] = false,
            ["tvSystem"] = "NTSC",
            ["romSizeBytes"] = 18,
        }));
    }

    [TestCase((byte)0x40, (byte)0x00, (byte)0x00, 4, TestName = "iNES mapper from the byte 6 nibble")]
    [TestCase((byte)0x10, (byte)0x40, (byte)0x00, 0x41, TestName = "iNES mapper adds the byte 7 nibble")]
    [TestCase((byte)0x10, (byte)0x40, (byte)0x0F, 0x41, TestName = "iNES ignores byte 8")]
    [TestCase((byte)0x10, (byte)0x48, (byte)0x02, 0x241, TestName = "NES 2.0 mapper adds byte 8")]
    [TestCase((byte)0x10, (byte)0x48, (byte)0x00, 0x41, TestName = "NES 2.0 mapper with byte 8 zero")]
    public void Mapper(byte b6, byte b7, byte b8, int mapper) =>
        Assert.That(Field(Header(b6: b6, b7: b7, b8: b8), "mapper"), Is.EqualTo(mapper));

    [Test]
    public void Format_IsNes20OnlyForBits10()
    {
        Assert.That(Field(Header(b7: 0x08), "format"), Is.EqualTo("NES 2.0"));
        Assert.That(Field(Header(b7: 0x00), "format"), Is.EqualTo("iNES"));
        Assert.That(Field(Header(b7: 0x04), "format"), Is.EqualTo("iNES"), "01b is archaic iNES");
        Assert.That(Field(Header(b7: 0x0C), "format"), Is.EqualTo("iNES"), "11b is not NES 2.0");
    }

    [Test]
    public void Sizes_INes_UseBytes4And5Only()
    {
        var rom = Header(b4: 16, b5: 0, b9: 0x21);
        Assert.That((Field(rom, "prgRomSizeBytes"), Field(rom, "chrRomSizeBytes")), Is.EqualTo((262144, 0)));
    }

    [Test]
    public void Sizes_Nes20_AddTheByte9Nibbles()
    {
        var rom = Header(b4: 0x01, b5: 0x02, b7: 0x08, b9: 0x21);
        Assert.That((Field(rom, "prgRomSizeBytes"), Field(rom, "chrRomSizeBytes")), Is.EqualTo(((0x101) * 16384, (0x202) * 8192)));
    }

    [Test]
    public void Sizes_Nes20ExponentForm_IsNull()
    {
        var rom = Header(b7: 0x08, b9: 0xFF);
        Assert.That((Field(rom, "prgRomSizeBytes"), Field(rom, "chrRomSizeBytes")), Is.EqualTo(((object?)null, (object?)null)));
        Assert.That(Field(Header(b7: 0x08, b9: 0x0F), "chrRomSizeBytes"), Is.EqualTo(8192), "only the PRG nibble is 0xF");
    }

    [TestCase((byte)0x00, "Horizontal")]
    [TestCase((byte)0x01, "Vertical")]
    [TestCase((byte)0x08, "Four-screen")]
    [TestCase((byte)0x09, "Four-screen")]
    public void Mirroring(byte b6, string mirroring) => Assert.That(Field(Header(b6: b6), "mirroring"), Is.EqualTo(mirroring));

    [Test]
    public void BatteryAndTrainer()
    {
        Assert.That((Field(Header(b6: 0x02), "battery"), Field(Header(b6: 0x02), "trainer")), Is.EqualTo((true, false)));
        Assert.That((Field(Header(b6: 0x04), "battery"), Field(Header(b6: 0x04), "trainer")), Is.EqualTo((false, true)));
    }

    [TestCase((byte)0x00, (byte)0, "NTSC", TestName = "iNES NTSC")]
    [TestCase((byte)0x00, (byte)1, "PAL", TestName = "iNES PAL")]
    [TestCase((byte)0x08, (byte)0, "NTSC", TestName = "NES 2.0 NTSC")]
    [TestCase((byte)0x08, (byte)1, "PAL", TestName = "NES 2.0 PAL")]
    [TestCase((byte)0x08, (byte)2, "Multi-region", TestName = "NES 2.0 multi-region")]
    [TestCase((byte)0x08, (byte)3, "Dendy", TestName = "NES 2.0 Dendy")]
    public void TvSystem(byte b7, byte value, string tv)
    {
        // iNES reads byte 9 bit 0, NES 2.0 byte 12; each format ignores the other's byte.
        var rom = b7 == 0x08 ? Header(b7: b7, b12: value, b9: 0x00) : Header(b7: b7, b9: value, b12: 3);
        Assert.That(Field(rom, "tvSystem"), Is.EqualTo(tv));
    }

    [Test]
    public void BadMagic_OnlyHeaderValidAndSize()
    {
        var rom = Header(b4: 8, b6: 0x13);
        rom[3] = 0x00;

        var header = NesHeaderReader.Read(rom)!;

        Assert.That(header.IsPlausible, Is.False);
        Assert.That(header.Fields.Where(f => f.Value is not null).Select(f => (f.Key, f.Value)),
            Is.EqualTo(new (string, object?)[] { ("headerValid", false), ("romSizeBytes", 16) }));
        Assert.That(header.Fields.Keys, Has.Count.EqualTo(10));
    }

    [Test]
    public void RandomGarbage_NeverThrows()
    {
        var random = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var rom = new byte[random.Next(0, 64)];
            random.NextBytes(rom);
            if (i % 2 == 0 && rom.Length >= 4)
                "NES\x1A"u8.CopyTo(rom);
            Assert.DoesNotThrow(() => NesHeaderReader.Read(rom));
        }
    }
}
