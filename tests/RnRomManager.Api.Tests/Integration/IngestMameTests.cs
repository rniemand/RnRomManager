using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Arcade I2–I5, G1–G3, G7 through ingest: set ZIPs matched by set name and CRCs against a fixture MAME DAT.</summary>
[Category("Integration")]
public class IngestMameTests
{
    private ApiFactory? _shared;

    private ApiFactory Factory => _shared ?? throw new InvalidOperationException("not started");

    // Parent: two ROMs. Clone: one own ROM and one merged from the parent. Partial: two ROMs, tests drop one.
    private static readonly (string Name, string Content)[] ParentRoms = [("a.bin", "parent-a"), ("b.bin", "parent-b")];
    private static readonly (string Name, string Content)[] CloneRoms = [("u.bin", "clone-u")];

    private static string Crc(string content) => Crc32.ToHex(Encoding.ASCII.GetBytes(content));

    private static readonly string Dat = $"""
        <?xml version="1.0"?>
        <mame build="0.78">
        	<game name="rnfight">
        		<description>Rn Fight (World 910522)</description>
        		<year>1991</year>
        		<manufacturer>Rn Co</manufacturer>
        		<rom name="a.bin" size="8" crc="{Crc("parent-a")}"/>
        		<rom name="b.bin" size="8" crc="{Crc("parent-b")}"/>
        	</game>
        	<game name="rnfightu" cloneof="rnfight" romof="rnfight">
        		<description>Rn Fight (US 910206)</description>
        		<year>19??</year>
        		<manufacturer>Rn Co</manufacturer>
        		<rom name="u.bin" size="7" crc="{Crc("clone-u")}"/>
        		<rom name="b.bin" merge="b.bin" size="8" crc="{Crc("parent-b")}"/>
        	</game>
        	<game name="rnpart">
        		<description>Rn Part (Japan)</description>
        		<rom name="x.bin" size="1" crc="{Crc("x")}"/>
        		<rom name="y.bin" size="1" crc="{Crc("y")}"/>
        	</game>
        </mame>
        """;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _shared = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _shared.Settings["Sources:ThumbnailBaseUrl"] = ""; // default art, no network
        FixtureDatWriter.Write(Path.Combine(_shared.Root, "cache", DatCache.FolderName), new Dictionary<string, IReadOnlyList<FixtureGame>>(),
            mame: new Dictionary<string, string> { ["MAME"] = Dat });
        await _shared.StartSharedAsync();
    }

    [SetUp]
    public Task Reset() => Factory.ResetAsync();

    [OneTimeTearDown]
    public void StopHost() => _shared?.Dispose();

    private T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    private string Bucket => Path.Combine(Factory.Root, "roms", "mame2003", "R");

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(1991, 5, 22, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.ASCII.GetBytes(content));
            }
        }
        return buffer.ToArray();
    }

    private IngestFile Drop(string name, byte[] bytes)
    {
        var path = Path.Combine(Factory.Root, "ingest", "mame2003", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var info = new FileInfo(path);
        return new IngestFile("mame2003", path, name, info.Length, info.LastWriteTimeUtc);
    }

    private Task<IngestItemResult?> IngestAsync(IngestFile file) =>
        Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new AuditActor(null, null, Guid.NewGuid()), new CircuitBreaker(), CancellationToken.None);

    private async Task<IngestItemResult> IngestedAsync(string name, byte[] bytes)
    {
        var result = await IngestAsync(Drop(name, bytes));
        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Ingested, (string?)null)), result?.Details);
        return result!;
    }

    private InfoJsonGame ReadGame()
    {
        using var library = LibraryZip.Open(Path.Combine(Bucket, "Rn Fight.zip"), Service<InfoJsonReader>());
        return library.InfoJson!.Game!;
    }

    private async Task<ReviewItem> ReviewOfAsync(byte[] bytes) =>
        (await Service<IIngestReviewRepository>().GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;

    private static string? Text(InfoJsonRom rom, string key) => rom.Header![key]?.GetValue<string>();

    [Test]
    public async Task CompleteParent_IsANewGame_StoredBesideTheZipUnchanged_WithHeaderAndMetadata()
    {
        var bytes = Zip(ParentRoms);

        var result = await IngestedAsync("rnfight.zip", bytes);

        Assert.That(result.SystemId, Is.EqualTo("mame2003"));
        var game = ReadGame();
        Assert.That((game.Title, game.ReleaseYear, game.Developers, game.Publishers),
            Is.EqualTo(("Rn Fight", (int?)1991, (IReadOnlyList<string>)["Rn Co"], (IReadOnlyList<string>)["Rn Co"])));
        var rom = game.Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.OriginalFileName, rom.IsPrimary),
            Is.EqualTo(("rnfight.mamez", (string?)"Rn Fight.rnfight.mamez", "rnfight.zip", true)));
        Assert.That((rom.Regions.Count, rom.Languages.Count, rom.Revision, rom.Flags.Count), Is.EqualTo((0, 0, (string?)null, 0)));
        Assert.That(rom.Identification, Is.EqualTo(new InfoJsonIdentification(IdentificationMethod.Dat, "MAME", FixtureDatWriter.MameVersion, "Rn Fight (World 910522)")));
        Assert.That(rom.Header!.ToJsonString(), Is.EqualTo("""{"setName":"rnfight","cloneOf":null,"romOf":null,"isBios":false,"selfContained":true}"""));
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, "Rn Fight.rnfight.mamez")), Is.EqualTo(bytes));
        Assert.That(Directory.GetFiles(Path.Combine(Factory.Root, "ingest", "mame2003")), Is.Empty);
    }

    [Test]
    public async Task SplitClone_JoinsItsParentsGame_AndTheParentIsPrimary()
    {
        var parent = await IngestedAsync("rnfight.zip", Zip(ParentRoms));

        var clone = await IngestedAsync("rnfightu.zip", Zip(CloneRoms));

        Assert.That(clone.GameId, Is.EqualTo(parent.GameId));
        var roms = ReadGame().Roms;
        Assert.That(roms.Select(r => (r.FileName, r.IsPrimary)), Is.EquivalentTo(new[] { ("rnfight.mamez", true), ("rnfightu.mamez", false) }));
        var cloneRom = roms.Single(r => r.FileName == "rnfightu.mamez");
        Assert.That((Text(cloneRom, "setName"), Text(cloneRom, "cloneOf"), Text(cloneRom, "romOf")), Is.EqualTo(("rnfightu", "rnfight", "rnfight")));
        Assert.That(cloneRom.Header!["selfContained"]!.GetValue<bool>(), Is.False, "the merged ROM is in the parent's ZIP");
        Assert.That(cloneRom.Identification.DatGameName, Is.EqualTo("Rn Fight (US 910206)"));

        // A deep integrity run reads both .mamez files as the game's external ROMs: only the placeholder box art is open.
        var started = await Service<JobRunner>().StartAsync(Service<RnRomManager.Core.Integrity.IntegrityJobFactory>()(JobMode.Deep, null), JobTrigger.Manual, null, default);
        await started!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var open = (await Service<RnRomManager.Core.Integrity.IIntegrityIssueRepository>().ListAllOpenAsync(default)).Select(i => i.Type).ToList();
        Assert.That(open, Is.EqualTo(new[] { RnRomManager.Core.Integrity.IntegrityIssueType.DefaultBoxArt }));
    }

    [Test]
    public async Task CloneBeforeItsParent_GroupsUnderTheParentsBaseTitle()
    {
        var clone = await IngestedAsync("rnfightu.zip", Zip([.. CloneRoms, ("b.bin", "parent-b")]));
        var parent = await IngestedAsync("rnfight.zip", Zip(ParentRoms));

        Assert.That(parent.GameId, Is.EqualTo(clone.GameId));
        var game = ReadGame();
        Assert.That(game.Title, Is.EqualTo("Rn Fight"));
        Assert.That(game.Roms.Single(r => r.IsPrimary).FileName, Is.EqualTo("rnfight.mamez"), "parents rank ahead of clones (G7)");
        Assert.That(game.Roms.Single(r => r.FileName == "rnfightu.mamez").Header!["selfContained"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task UpperCaseSourceName_IsStoredUnderTheDatSpelling()
    {
        await IngestedAsync("RNFIGHT.ZIP", Zip(ParentRoms));

        var rom = ReadGame().Roms.Single();
        Assert.That((rom.FileName, Text(rom, "setName"), rom.OriginalFileName), Is.EqualTo(("rnfight.mamez", "rnfight", "RNFIGHT.ZIP")));
    }

    [Test]
    public async Task RenamedEntries_StillMatch_ByCrc()
    {
        await IngestedAsync("rnfight.zip", Zip(("one", "parent-a"), ("roms/two", "parent-b"), ("readme.txt", "extra")));

        Assert.That(ReadGame().Roms.Single().FileName, Is.EqualTo("rnfight.mamez"));
    }

    [Test]
    public async Task IncompleteSet_GoesToReview_WithTheMissingRoms()
    {
        var bytes = Zip(("x.bin", "x"));

        var result = await IngestAsync(Drop("rnpart.zip", bytes));

        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var item = await ReviewOfAsync(bytes);
        Assert.That(item.SystemId, Is.EqualTo("mame2003"));
        Assert.That(JsonDocument.Parse(item.Header!).RootElement.ToString(),
            Is.EqualTo("""{"setName":"rnpart","description":"Rn Part (Japan)","missingRoms":"y.bin"}"""));
        Assert.That(item.Suggestions, Is.EqualTo("[]"));
    }

    [Test]
    public async Task UnknownSetName_GoesToReview_WithoutAHeader()
    {
        var bytes = Zip(ParentRoms);

        var result = await IngestAsync(Drop("street fighter.zip", bytes));

        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var item = await ReviewOfAsync(bytes);
        Assert.That((item.Header, item.Suggestions), Is.EqualTo(((string?)null, "[]")));
    }

    [Test]
    public async Task NotAZip_FailsAsInvalidHeader()
    {
        var file = Drop("rnfight.zip", Encoding.ASCII.GetBytes("this is plainly not a zip archive"));

        var result = await IngestAsync(file);

        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Failed, (string?)IngestFailures.InvalidHeader)));
        Assert.That(result!.Details, Does.StartWith("not a readable ZIP: "));
    }

    [Test]
    public async Task SameZipTwice_IsADuplicate()
    {
        var bytes = Zip(ParentRoms);
        await IngestedAsync("rnfight.zip", bytes);

        var again = await IngestAsync(Drop("rnfight.zip", bytes));

        Assert.That(again?.Outcome, Is.EqualTo(IngestOutcome.Duplicate));
        Assert.That(ReadGame().Roms, Has.Count.EqualTo(1));
    }
}
