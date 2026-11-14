using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Large ROM entries are written with <see cref="CompressionLevel.Fastest"/>, everything else with <see cref="CompressionLevel.Optimal"/>.</summary>
public class LibraryZipCompressionTests
{
    private const long MiB = 1024 * 1024;
    private const string RomEntry = "roms/Big (World).gb";
    private static readonly DateTimeOffset When = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Rom = CompressibleText(2 * (int)MiB);
    private static readonly byte[] Png = PngTests.Png(1, 1);

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var handler = Substitute.For<ISystemHandler>();
        handler.EmbedRomsInZip.Returns(true);
        handler.Id.Returns("gb");
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns([handler]);
        _reader = new InfoJsonReader([new InfoJsonUpgraderV1(), new InfoJsonUpgraderV2(), new InfoJsonUpgraderV3(), new InfoJsonUpgraderV4(),
            new InfoJsonUpgraderV5(), new InfoJsonUpgraderV6()], registry);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    [Test]
    public void CompressionFor_IsFastestOnlyForRomEntriesAboveTheThreshold()
    {
        const long limit = LibraryZipWriter.DefaultFastCompressionAboveBytes;
        Assert.That(limit, Is.EqualTo(128 * MiB));
        Assert.That(LibraryZipWriter.CompressionFor(RomEntry, 128 * MiB, limit), Is.EqualTo(CompressionLevel.Optimal));
        Assert.That(LibraryZipWriter.CompressionFor(RomEntry, 128 * MiB + 1, limit), Is.EqualTo(CompressionLevel.Fastest));
        Assert.That(LibraryZipWriter.CompressionFor("images/boxart-01.png", 1024 * MiB, limit), Is.EqualTo(CompressionLevel.Optimal));
        Assert.That(LibraryZipWriter.CompressionFor(LibraryZip.InfoJsonEntry, 1024 * MiB, limit), Is.EqualTo(CompressionLevel.Optimal));
    }

    [Test]
    public async Task LoweredThreshold_WritesTheRomFastest_RoundTripsAndVerifies()
    {
        var fast = await WriteAsync("fast.zip", new LibraryZipWriter(_reader) { FastCompressionAboveBytes = MiB });
        var optimal = await WriteAsync("optimal.zip", new LibraryZipWriter(_reader));

        Assert.That(Read(fast), Is.EqualTo(Rom), "the write verified and the entry round-trips");
        Assert.That(CompressedLength(fast), Is.GreaterThan(CompressedLength(optimal)), "Fastest compresses less than Optimal");
        Assert.That(CompressedLength(optimal), Is.LessThan(Rom.Length), "the text is compressible");
    }

    [Test]
    public async Task RewriteAddingAnImage_KeepsTheLargeRomEntryByteForByte()
    {
        var writer = new LibraryZipWriter(_reader) { FastCompressionAboveBytes = MiB };
        var path = await WriteAsync("game.zip", writer);
        var compressed = CompressedLength(path);

        using (var existing = LibraryZip.Open(path, _reader))
        {
            await writer.WriteAsync(path, existing, new LibraryZipChanges(Game([BoxArt()]), new Dictionary<string, Func<Stream>>
            {
                ["images/boxart-01.png"] = () => new MemoryStream(Png),
            }));
        }

        Assert.That(Read(path), Is.EqualTo(Rom));
        Assert.That(CompressedLength(path), Is.EqualTo(compressed), "re-written at Fastest again");
        using var zip = LibraryZip.Open(path, _reader);
        Assert.That(zip.HasEntry("images/boxart-01.png"), Is.True);
    }

    private async Task<string> WriteAsync(string name, LibraryZipWriter writer)
    {
        var path = _dir.Combine(name);
        await writer.WriteAsync(path, null, new LibraryZipChanges(Game([]), new Dictionary<string, Func<Stream>>
        {
            [RomEntry] = () => new MemoryStream(Rom),
        }));
        return path;
    }

    private static byte[] Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(RomEntry)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static long CompressedLength(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry(RomEntry)!.CompressedLength;
    }

    /// <summary>Words from a fixed list, drawn by a seeded <see cref="Random"/>: compressible, and better compressed by Optimal.</summary>
    private static byte[] CompressibleText(int length)
    {
        string[] words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima",
            "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray"];
        var random = new Random(14);
        var text = new StringBuilder(length + 16);
        while (text.Length < length)
            text.Append(words[random.Next(words.Length)]).Append(' ');
        return Encoding.ASCII.GetBytes(text.ToString(0, length));
    }

    private static InfoJsonImage BoxArt() => new()
    {
        FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceDefault, SourceUrl = null, IsDefault = true,
        IsCover = true, SizeBytes = Png.Length, Sha1 = Convert.ToHexStringLower(SHA1.HashData(Png)), Width = 1, Height = 1, DateAdded = When,
    };

    private static InfoJsonGame Game(IReadOnlyList<InfoJsonImage> images) => new()
    {
        SchemaVersion = InfoJsonReader.CurrentVersion, GameId = Guid.Parse("0c7d2f4e-5d1b-4a8e-9f3a-2b6c1d4e5f60"), System = "gb", Title = "Big",
        DisplayTitle = "Big", ReleaseYear = null, Developers = [], Publishers = [], Genres = [], Description = null, Rating = null,
        Favourite = false, Images = images, Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
        Roms =
        [
            new InfoJsonRom
            {
                FileName = "Big (World).gb", OriginalFileName = "Big (World).gb", BaseTitle = "Big", Regions = ["World"], Languages = [],
                Revision = null, Flags = [], IsPrimary = true, Identification = new InfoJsonIdentification(IdentificationMethod.Manual), Header = null,
                Metadata = new InfoJsonRomMetadata(Rom.Length, Crc32.ToHex(Rom), Convert.ToHexStringLower(MD5.HashData(Rom)),
                    Convert.ToHexStringLower(SHA1.HashData(Rom)), When, When, "/app/ingest/gb/Big (World).gb"),
            },
        ],
    };
}
