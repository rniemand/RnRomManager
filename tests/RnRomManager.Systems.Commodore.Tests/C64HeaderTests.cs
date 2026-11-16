using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>C64 B2: the four container headers, read through <see cref="C64Handler.ReadHeader"/>.</summary>
public class C64HeaderTests
{
    private static readonly ISystemHandler Handler = new C64Handler();

    private static byte[] WithMagic(string magic, int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Crt(string title, int size = 64)
    {
        var bytes = WithMagic("C64 CARTRIDGE   ", size);
        bytes[0x14] = 1; bytes[0x15] = 0;
        bytes[0x16] = 0x01; bytes[0x17] = 0x02; // hardware type 0x0102, big-endian
        bytes[0x18] = 0; bytes[0x19] = 1;
        Encoding.Latin1.GetBytes(title).CopyTo(bytes, 0x20);
        return bytes;
    }

    [Test]
    public void Crt_ReadsEveryField()
    {
        var header = Handler.ReadHeader(Crt("PIXEL PILOT"))!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("c64", "PIXEL PILOT", true)));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[] { "format", "version", "hardwareType", "exrom", "game", "title" }));
        Assert.That(header.Fields["format"], Is.EqualTo("crt"));
        Assert.That(header.Fields["version"], Is.EqualTo("1.0"));
        Assert.That(header.Fields["hardwareType"], Is.EqualTo(0x0102));
        Assert.That((header.Fields["exrom"], header.Fields["game"]), Is.EqualTo((0, 1)));
        Assert.That(header.Fields["title"], Is.EqualTo("PIXEL PILOT"));
    }

    [Test]
    public void Crt_EmptyTitle_IsNull()
    {
        var header = Handler.ReadHeader(Crt("   "))!;

        Assert.That((header.Title, header.Fields["title"]), Is.EqualTo(((string?)null, (object?)null)));
    }

    [Test]
    public void Crt_Title_StopsAtTheFirstNul_AndIsTrimmed()
    {
        var bytes = Crt("  ACE  \0JUNK");

        Assert.That(Handler.ReadHeader(bytes)!.Title, Is.EqualTo("ACE"));
    }

    [Test]
    public void Crt_Title_Is32BytesWithoutANul()
    {
        var bytes = Crt(new string('A', 32), size: 80);
        bytes[0x40] = (byte)'B';

        Assert.That(Handler.ReadHeader(bytes)!.Title, Is.EqualTo(new string('A', 32)));
    }

    [Test]
    public void Tap_ReadsFormatAndVersion()
    {
        var bytes = WithMagic("C64-TAPE-RAW", 20);
        bytes[0x0C] = 1;

        var header = Handler.ReadHeader(bytes)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("c64", (string?)null, true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?> { ["format"] = "tap", ["version"] = 1 }));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[] { "format", "version" }));
    }

    [Test]
    public void G64_ReadsFormatVersionAndTracks()
    {
        var bytes = WithMagic("GCR-1541", 12);
        bytes[0x08] = 0; bytes[0x09] = 84;

        var header = Handler.ReadHeader(bytes)!;

        Assert.That((header.ClassifiedSystemId, header.Title), Is.EqualTo(("c64", (string?)null)));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[] { "format", "version", "tracks" }));
        Assert.That(header.Fields.Values, Is.EqualTo(new object?[] { "g64", 0, 84 }));
    }

    [Test]
    public void Nib_ReadsFormatAndVersion()
    {
        var bytes = WithMagic("MNIB-1541-RAW", 14);
        bytes[0x0D] = 3;

        var header = Handler.ReadHeader(bytes)!;

        Assert.That((header.ClassifiedSystemId, header.Title), Is.EqualTo(("c64", (string?)null)));
        Assert.That(header.Fields.Keys, Is.EqualTo(new[] { "format", "version" }));
        Assert.That(header.Fields.Values, Is.EqualTo(new object?[] { "nib", 3 }));
    }

    [Test]
    public void NumbersAreReadUnsigned()
    {
        var bytes = WithMagic("C64-TAPE-RAW", 20);
        bytes[0x0C] = 0xFF;

        Assert.That(Handler.ReadHeader(bytes)!.Fields["version"], Is.EqualTo(255));
    }

    [TestCase("C64 CARTRIDGE   ", 63)]
    [TestCase("C64-TAPE-RAW", 19)]
    [TestCase("GCR-1541", 11)]
    [TestCase("MNIB-1541-RAW", 13)]
    public void OneByteShortOfTheMinimum_IsNull(string magic, int size) =>
        Assert.That(Handler.ReadHeader(WithMagic(magic, size)), Is.Null);

    [Test]
    public void PrgLikeData_IsNull()
    {
        var bytes = new byte[2010];
        new Random(2010).NextBytes(bytes);
        bytes[0] = 0x01; bytes[1] = 0x08; // load address $0801

        Assert.That(Handler.ReadHeader(bytes), Is.Null);
    }

    [TestCase(0)]
    [TestCase(174848)]
    public void ZeroFilledData_IsNull(int size) => Assert.That(Handler.ReadHeader(new byte[size]), Is.Null);
}
