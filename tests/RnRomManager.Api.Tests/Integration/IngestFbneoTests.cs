using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Arcade (FinalBurn Neo) I1–I4, G1: <c>.zip</c> and <c>.7z</c> sets matched against a fixture FBNeo DAT.</summary>
[Category("Integration")]
public class IngestFbneoTests
{
    private ApiFactory? _shared;

    private ApiFactory Factory => _shared ?? throw new InvalidOperationException("not started");

    private static readonly (string Name, string Content)[] ParentRoms = [("p1.bin", "fb-parent-1"), ("p2.bin", "fb-parent-2")];

    private static string Crc(string content) => Crc32.ToHex(Encoding.ASCII.GetBytes(content));

    // The split clone's one ROM is basic.7z's entry, so that fixture is the clone's set 7z.
    private static readonly string Dat = $"""
        <?xml version="1.0"?>
        <datafile>
        	<game name="rnfbp">
        		<description>Rn FB Parent (World)</description>
        		<year>1994</year>
        		<manufacturer>Rn Co</manufacturer>
        		<rom name="p1.bin" size="11" crc="{Crc("fb-parent-1")}"/>
        		<rom name="p2.bin" size="11" crc="{Crc("fb-parent-2")}"/>
        	</game>
        	<game name="rnfbc" cloneof="rnfbp" romof="rnfbp">
        		<description>Rn FB Parent (Japan)</description>
        		<year>1994</year>
        		<manufacturer>Rn Co</manufacturer>
        		<rom name="{TestFixtures.Basic7zEntry}" size="{TestFixtures.Basic7zRom().Length}" crc="{Crc32.ToHex(TestFixtures.Basic7zRom())}"/>
        		<rom name="p2.bin" merge="p2.bin" size="11" crc="{Crc("fb-parent-2")}"/>
        	</game>
        	<game name="rnbios" isbios="yes">
        		<description>Rn BIOS</description>
        		<rom name="bios.bin" size="7" crc="{Crc("fb-bios")}"/>
        	</game>
        </datafile>
        """;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _shared = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _shared.Settings["Sources:ThumbnailBaseUrl"] = ""; // default art, no network
        FixtureDatWriter.Write(Path.Combine(_shared.Root, "cache", DatCache.FolderName), new Dictionary<string, IReadOnlyList<FixtureGame>>(),
            mame: new Dictionary<string, string> { ["MAME"] = IngestSeeder.MinimalMame["MAME"], [FbneoDatName] = Dat });
        await _shared.StartSharedAsync();
    }

    private const string FbneoDatName = "FBNeo - Arcade Games";

    [SetUp]
    public Task Reset() => Factory.ResetAsync();

    [OneTimeTearDown]
    public void StopHost() => _shared?.Dispose();

    private T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    private string Bucket => Path.Combine(Factory.Root, "roms", "fbneo", "R");

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(1994, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.ASCII.GetBytes(content));
            }
        }
        return buffer.ToArray();
    }

    private IngestFile Drop(string name, byte[] bytes)
    {
        var path = Path.Combine(Factory.Root, "ingest", "fbneo", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var info = new FileInfo(path);
        return new IngestFile("fbneo", path, name, info.Length, info.LastWriteTimeUtc);
    }

    private Task<IngestItemResult?> IngestAsync(IngestFile file) =>
        Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new AuditActor(null, null, Guid.NewGuid()), new CircuitBreaker(), CancellationToken.None);

    private async Task<IngestItemResult> IngestedAsync(string name, byte[] bytes)
    {
        var result = await IngestAsync(Drop(name, bytes));
        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Ingested, (string?)null)), result?.Details);
        return result!;
    }

    private InfoJsonGame ReadGame(string zipName = "Rn FB Parent.zip")
    {
        using var library = LibraryZip.Open(Path.Combine(Bucket, zipName), Service<InfoJsonReader>());
        return library.InfoJson!.Game!;
    }

    private static string? Text(InfoJsonRom rom, string key) => rom.Header![key]?.GetValue<string>();

    [Test]
    public async Task Parent_Zip_IsANewGame_StoredByteForByte()
    {
        var bytes = Zip(ParentRoms);

        var result = await IngestedAsync("rnfbp.zip", bytes);

        Assert.That(result.SystemId, Is.EqualTo("fbneo"));
        var game = ReadGame();
        Assert.That((game.Title, game.ReleaseYear), Is.EqualTo(("Rn FB Parent", (int?)1994)));
        var rom = game.Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.OriginalFileName, rom.IsPrimary),
            Is.EqualTo(("rnfbp.fbneoz", (string?)"Rn FB Parent.rnfbp.fbneoz", "rnfbp.zip", true)));
        Assert.That(rom.Identification, Is.EqualTo(new InfoJsonIdentification(IdentificationMethod.Dat, FbneoDatName, FixtureDatWriter.MameVersion, "Rn FB Parent (World)")));
        Assert.That(rom.Header!.ToJsonString(), Is.EqualTo("""{"setName":"rnfbp","cloneOf":null,"romOf":null,"isBios":false,"selfContained":true}"""));
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, "Rn FB Parent.rnfbp.fbneoz")), Is.EqualTo(bytes));
    }

    [Test]
    public async Task SplitClone_7z_JoinsTheParentsGame()
    {
        var parent = await IngestedAsync("rnfbp.zip", Zip(ParentRoms));
        var bytes = File.ReadAllBytes(TestFixtures.Basic7zPath);

        var clone = await IngestedAsync("rnfbc.7z", bytes);

        Assert.That(clone.GameId, Is.EqualTo(parent.GameId));
        var roms = ReadGame().Roms;
        Assert.That(roms.Select(r => (r.FileName, r.IsPrimary)), Is.EquivalentTo(new[] { ("rnfbp.fbneoz", true), ("rnfbc.fbneo7z", false) }));
        var cloneRom = roms.Single(r => r.FileName == "rnfbc.fbneo7z");
        Assert.That((Text(cloneRom, "setName"), Text(cloneRom, "cloneOf"), cloneRom.ExternalFileName), Is.EqualTo(("rnfbc", "rnfbp", (string?)"Rn FB Parent.rnfbc.fbneo7z")));
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, "Rn FB Parent.rnfbc.fbneo7z")), Is.EqualTo(bytes));
    }

    [Test]
    public async Task UpperCaseName_KeepsTheDatSpelling()
    {
        await IngestedAsync("RNFBP.ZIP", Zip(ParentRoms));

        var rom = ReadGame().Roms.Single();
        Assert.That((rom.FileName, Text(rom, "setName"), rom.OriginalFileName), Is.EqualTo(("rnfbp.fbneoz", "rnfbp", "RNFBP.ZIP")));
    }

    [Test]
    public async Task Incomplete_GoesToReviewWithMissingRoms()
    {
        var bytes = Zip(ParentRoms[0]);

        var result = await IngestAsync(Drop("rnfbp.zip", bytes));

        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var item = (await Service<IIngestReviewRepository>().GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
        Assert.That(item.SystemId, Is.EqualTo("fbneo"));
        Assert.That(JsonDocument.Parse(item.Header!).RootElement.ToString(),
            Is.EqualTo("""{"setName":"rnfbp","description":"Rn FB Parent (World)","missingRoms":"p2.bin"}"""));
    }

    [Test]
    public async Task NotA7z_FailsInvalidHeader()
    {
        var text = Encoding.ASCII.GetBytes("this is plainly not a 7z archive");
        Assert.That(text, Has.Length.EqualTo(32));

        var result = await IngestAsync(Drop("rnfbp.7z", text));

        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Failed, (string?)IngestFailures.InvalidHeader)));
        Assert.That(result!.Details, Does.StartWith("not a readable 7z: "));
    }

    [Test]
    public async Task BiosSet_IsFlaggedBios()
    {
        await IngestedAsync("rnbios.zip", Zip(("bios.bin", "fb-bios")));

        var rom = ReadGame("Rn BIOS.zip").Roms.Single();
        Assert.That((rom.FileName, rom.Flags), Is.EqualTo(("rnbios.fbneoz", (IReadOnlyList<string>)["BIOS"])));
    }
}
