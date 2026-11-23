using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.NintendoDs.Tests;

public class NintendoDsHandlerTests
{
    private static readonly ISystemHandler Handler = new NintendoDsHandler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("nds", "Nintendo DS", "DS", "nds", "Nintendo - Nintendo DS")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".nds", ".ids" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((512L, 536_870_912L)));
        Assert.That((Handler.EmbedRomsInZip, Handler.LegacyEmbeddedRoms, Handler.FileSets, Handler.DatSource, Handler.DatFolder),
            Is.EqualTo((false, true, false, DatSource.Libretro, DatKind.NoIntro)), "S1: stored next to the ZIP; older games until migrated");
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
        Assert.That(Handler.MetadatKinds, Has.Count.EqualTo(4));
        Assert.That(Handler.BrowserCore, Is.EqualTo("desmume"));
    }

    [Test]
    public void RomStorage_IsConfigurable_DefaultBesideTheZip_FollowsTheOverride()
    {
        Assert.That((Handler.RomStorageConfigurable, Handler.DefaultEmbedRomsInZip), Is.EqualTo((true, false)));
        var settings = Substitute.For<IRomStorageSettings>();
        ISystemHandler handler = new NintendoDsHandler(settings);
        Assert.That(handler.EmbedRomsInZip, Is.False, "no stored value");
        settings.Override("nds").Returns(true);
        Assert.That((handler.EmbedRomsInZip, handler.DefaultEmbedRomsInZip), Is.EqualTo((true, false)));
        settings.Override("nds").Returns(false);
        Assert.That(handler.EmbedRomsInZip, Is.False);
    }

    [Test]
    public void Normalization_ReturnsTheBytesUnchanged()
    {
        var rom = new SyntheticNdsRomBuilder { Seed = 1 }.Build();
        var normalized = Handler.NormalizeForHashing(rom);
        Assert.That(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(normalized, out var segment)
            && ReferenceEquals(segment.Array, rom) && segment.Count == rom.Length, Is.True);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That(System.Text.Encoding.ASCII.GetString(png[12..16]), Is.EqualTo("IHDR"));
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }

    [Test]
    public void ReadHeader_ReadsTheSixFields()
    {
        var rom = new SyntheticNdsRomBuilder { Title = "MARKER TEST", GameCode = "AMKE", MakerCode = "01", UnitCode = 2, RomVersion = 3 }.Build();

        var header = Handler.ReadHeader(rom)!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("nds", "MARKER TEST", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["title"] = "MARKER TEST",
            ["gameCode"] = "AMKE",
            ["makerCode"] = "01",
            ["unitCode"] = 2,
            ["romVersion"] = 3,
            ["headerChecksumValid"] = true,
        }));
    }

    [Test]
    public void ReadHeader_ReplacesNonPrintableBytes_AndKeepsCodesWhole()
    {
        var rom = new SyntheticNdsRomBuilder().Build();
        rom[0] = (byte)'A';
        rom[1] = 0x7F;
        rom[2] = 0xE9;
        rom[3] = 0x00; // the title ends here
        rom[4] = (byte)'Z';
        "AB \u0001"u8.ToArray().CopyTo(rom, SyntheticNdsRomBuilder.GameCodeOffset);
        " \u0000"u8.ToArray().CopyTo(rom, SyntheticNdsRomBuilder.MakerCodeOffset);
        rom[SyntheticNdsRomBuilder.UnitCodeOffset] = 0xFF;

        var fields = Handler.ReadHeader(rom)!.Fields;

        Assert.That(fields["title"], Is.EqualTo("A??"));
        Assert.That(fields["gameCode"], Is.EqualTo("AB ?"), "no trimming");
        Assert.That(fields["makerCode"], Is.EqualTo(" ?"));
        Assert.That(fields["unitCode"], Is.EqualTo(255), "unsigned");
    }

    [TestCase("  PADDED    ", "  PADDED")]
    [TestCase("            ", null)]
    [TestCase(null, null)]
    public void ReadHeader_TrimsTrailingSpaces_AndAnEmptyTitleIsNull(string? stored, string? expected)
    {
        var header = Handler.ReadHeader(new SyntheticNdsRomBuilder { Title = stored }.Build())!;

        Assert.That(header.Title, Is.EqualTo(expected));
        Assert.That(header.Fields["title"], Is.EqualTo(expected));
    }

    /// <summary>0x15E bytes, zero except 0x15D = 1, have the CRC 0xDB0D (stored <c>0D DB</c>).</summary>
    private static byte[] CrcVector()
    {
        var rom = new byte[0x160];
        rom[0x15D] = 1;
        rom[0x15E] = 0x0D;
        rom[0x15F] = 0xDB;
        return rom;
    }

    [Test]
    public void HeaderCrc_KnownVector_IsPlausible()
    {
        var header = Handler.ReadHeader(CrcVector())!;
        Assert.That((header.IsPlausible, header.Fields["headerChecksumValid"]), Is.EqualTo((true, (object?)true)));
        Assert.That(NintendoDsHeaderReader.HeaderCrc(CrcVector().AsSpan(0, 0x15E)), Is.EqualTo(0xDB0D));
    }

    [Test]
    public void HeaderCrc_OneBitChangeInTheCoveredBytes_IsImplausible()
    {
        var rom = CrcVector();
        rom[0x15D] ^= 0x02;
        var header = Handler.ReadHeader(rom)!;
        Assert.That((header.IsPlausible, header.Fields["headerChecksumValid"]), Is.EqualTo((false, (object?)false)));
    }

    [Test]
    public void HeaderCrc_ChangeAfterTheChecksum_StaysPlausible()
    {
        var rom = new byte[0x200];
        CrcVector().CopyTo(rom, 0);
        rom[0x160] = 0xAA;
        rom[0x1FF] = 0x55;
        Assert.That(Handler.ReadHeader(rom)!.IsPlausible, Is.True);
    }

    [Test]
    public void ReadHeader_ShorterThan0x160_IsNull()
    {
        Assert.That(Handler.ReadHeader(CrcVector().AsSpan(0, 0x15F)), Is.Null);
    }

    [Test]
    public void Builder_BadCrc_IsImplausible_AndTheCrcCopiesAgree()
    {
        var bad = new SyntheticNdsRomBuilder { BadHeaderCrc = true }.Build();
        Assert.That(Handler.ReadHeader(bad)!.IsPlausible, Is.False);
        var span = CrcVector().AsSpan(0, 0x15E);
        Assert.That(SyntheticNdsRomBuilder.HeaderCrc(span), Is.EqualTo(NintendoDsHeaderReader.HeaderCrc(span)));
    }

    [Test]
    public void Builder_IsDeterministic_AndCanFillWithFF()
    {
        var a = new SyntheticNdsRomBuilder { Seed = 7, SizeBytes = 3 * 1024 * 1024 + 5 }.Build();
        Assert.That(a, Is.EqualTo(new SyntheticNdsRomBuilder { Seed = 7, SizeBytes = 3 * 1024 * 1024 + 5 }.Build()));
        Assert.That(a, Has.Length.EqualTo(3 * 1024 * 1024 + 5));
        Assert.That(a, Is.Not.EqualTo(new SyntheticNdsRomBuilder { Seed = 8, SizeBytes = 3 * 1024 * 1024 + 5 }.Build()));

        var ff = new SyntheticNdsRomBuilder { SizeBytes = 4096, FillWithFF = true }.Build();
        Assert.That(ff.AsSpan(SyntheticNdsRomBuilder.HeaderAreaBytes).ToArray(), Is.All.EqualTo(0xFF));
        Assert.That(Handler.ReadHeader(ff)!.IsPlausible, Is.True);
    }

    [Test]
    public void DatCache_LoadsADsCatalog_AndFindsTheGameBySha1()
    {
        using var dir = new TempDir();
        var rom = new SyntheticNdsRomBuilder { Seed = 3, Title = "DAT TEST" }.Build();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.NintendoDsDat] = [new FixtureGame("Dat Test (World)", "Dat Test (World).nds", rom) { ReleaseYear = 2005, Genres = ["Puzzle"] }],
        });

        cache.Load();

        var store = cache.For("nds")!;
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(rom));
        var hit = store.BySha1(sha1)!;
        Assert.That((hit.GameName, hit.RomName, hit.SizeBytes), Is.EqualTo(("Dat Test (World)", "Dat Test (World).nds", (long)rom.Length)));
        Assert.That(hit.Crc32, Is.EqualTo(Crc32.ToHex(rom)).IgnoreCase);
        Assert.That(hit.Md5, Is.EqualTo(Convert.ToHexStringLower(MD5.HashData(rom))).IgnoreCase);
        var metadata = store.MetadataFor(Crc32.ToHex(rom));
        Assert.That((metadata.ReleaseYear, metadata.Genres), Is.EqualTo(((int?)2005, (IReadOnlyList<string>)["Puzzle"])));
    }

    [Test]
    public void DatCache_LoadsAnEmptyDsCatalog()
    {
        using var dir = new TempDir();
        var cache = Cache(dir);
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [IngestSeeder.NintendoDsDat] = [] });

        cache.Load();

        Assert.That(cache.For("nds")!.Roms, Is.Empty);
    }

    private static DatCache Cache(TempDir dir) =>
        new(Options.Create(new PathsOptions { Ingest = dir.Combine("ingest"), Roms = dir.Combine("roms"), Cache = dir.Combine("cache") }),
            new SystemRegistry([Handler]), NullLogger<DatCache>.Instance);
}
