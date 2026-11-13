using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class IngestPipelineSerialTests
{
    private const string Title = "Wave Bowling (USA)";
    private const string ZipPath = "wii/W/Wave Bowling.zip";
    private static readonly AuditActor Actor = new(42, "boss", Guid.NewGuid());
    private static readonly SyntheticDiscBuilder Disc = new() { GameId = "RWBE01", Magic = NintendoDiscMagic.Wii };

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private WiiTestHandler _handler = null!;
    private IRomRepository _roms = null!;
    private IGameRepository _games = null!;
    private InfoJsonReader _reader = null!;
    private IngestPipeline _pipeline = null!;
    private DatCache _dats = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        Directory.CreateDirectory(Path.Combine(_paths.Ingest, "wii"));
        _handler = new WiiTestHandler { Header = Header() };
        var systems = new SystemRegistry([_handler]);
        var options = Options.Create(_paths);
        _dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        _roms = Substitute.For<IRomRepository>();
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([]);
        _roms.ListExternalAsync(default).ReturnsForAnyArgs([]);
        _games = Substitute.For<IGameRepository>();
        _games.ListZipPathEquivalenceAsync(default!, default).ReturnsForAnyArgs(c =>
            c.Arg<IReadOnlyList<string>>().Select(path => new ZipPathEquivalenceGroup([path], [])).ToList());
        _reader = new InfoJsonReader([], systems);
        _pipeline = new IngestPipeline(_dats, systems, _roms, _games, Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), Substitute.For<IIngestReviewRepository>(), _reader, TestMedia.Offline(),
            options, Substitute.For<IAuditLog>(), new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    [Test]
    public async Task UniqueGameId_StoresTheDatStemWithWbfsAndItsOwnHash()
    {
        LoadDat(Entry(Title + " (Disc 1)"));

        var result = await Import("bowling.WBFS", Disc.Wbfs());

        Assert.That(result.Outcome, Is.EqualTo(IngestOutcome.Ingested), result.Details);
        var rom = ReadGame().Roms.Single();
        Assert.That((rom.ExternalFileName, rom.Identification.Method, rom.Metadata.Sha1),
            Is.EqualTo((Title + " (Disc 1).wbfs", IdentificationMethod.Serial, result.Sha1)));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Disc 1" }));
    }

    [Test]
    public async Task DiscAndRevision_NarrowToOneEntry()
    {
        LoadDat(Entry(Title + " (Disc 1) (Rev 1)"), Entry(Title + " (Disc 2)"), Entry(Title + " (Disc 2) (Rev 1)"));
        _handler.Header = Header(disc: 1, version: 1);

        var result = await Import("bowling.wbfs", Disc.Wbfs());

        Assert.That(result.Outcome, Is.EqualTo(IngestOutcome.Ingested), result.Details);
        var rom = ReadGame().Roms.Single();
        Assert.That(rom.Identification.DatGameName, Is.EqualTo(Title + " (Disc 2) (Rev 1)"));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Disc 2" }));
        Assert.That(rom.Revision, Is.EqualTo("Rev 1"));
    }

    [TestCase("bowling.wbfs", "wbfs", "RWBE01", true)]
    [TestCase("bowling.wbfs", "wbfs", "NONE01", false)]
    [TestCase("bowling.wbfs", "iso", "RWBE01", false)]
    [TestCase("bowling.iso", "iso", "RWBE01", false)]
    [TestCase("bowling.iso", "wbfs", "RWBE01", false)]
    public async Task WithoutOneEligibleSerialMatch_WaitsInReview(string filename, string container, string id, bool ambiguous)
    {
        LoadDat(ambiguous ? [Entry(Title), Entry("Wave Bowling (Europe)")] : [Entry(Title)]);
        _handler.Header = Header(container, id);

        var result = await Import(filename, Disc.Wbfs());

        Assert.That((result.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
    }

    [Test]
    public async Task ExistingIsoOfTheSameEntry_GainsASecondRelease()
    {
        LoadDat(Entry(Title));
        _handler.Header = Header(container: "iso");
        var first = await Import("bowling.iso", Disc.Build());
        Assert.That(first.Outcome, Is.EqualTo(IngestOutcome.Ingested), first.Details);
        var game = ReadGame();
        _games.GetAsync(game.GameId, Arg.Any<CancellationToken>()).Returns(new GameRow
        {
            Id = game.GameId, SystemId = game.System, Title = game.Title, DisplayTitle = game.Title, ZipPath = ZipPath,
            Bucket = "W", Developers = [], Publishers = [], Genres = [], ZipSizeBytes = 0, ZipMtimeUtc = default,
            InfoSchemaVersion = InfoJsonReader.CurrentVersion, CreatedAt = default, UpdatedAt = default,
        });
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([game.GameId]);
        _handler.Header = Header();

        var result = await Import("bowling.wbfs", Disc.Wbfs());

        Assert.That((result.Outcome, result.GameId), Is.EqualTo((IngestOutcome.Ingested, first.GameId)), result.Details);
        Assert.That(ReadGame().Roms.Select(r => (r.ExternalFileName, r.Identification.Method)),
            Is.EquivalentTo(new[] { (Title + ".iso", IdentificationMethod.Dat), (Title + ".wbfs", IdentificationMethod.Serial) }));
    }

    private static RomHeader Header(string container = "wbfs", string id = "RWBE01", byte disc = 0, byte version = 0) =>
        new("wii", "WAVE BOWLING", true, new Dictionary<string, object?>
        {
            ["container"] = container, ["gameId"] = id, ["discNumber"] = disc, ["version"] = version,
        });

    private static FixtureGame Entry(string title) => new(title, title + ".iso", Disc.Build()) { Serial = "RVL-RWBE-USA" };

    private void LoadDat(params FixtureGame[] games)
    {
        FixtureDatWriter.Write(_dats.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>> { [_handler.DatName] = games },
            layouts: new Dictionary<string, FixtureDatLayout> { [_handler.DatName] = FixtureDatLayout.Redump });
        _dats.Load();
    }

    private async Task<IngestItemResult> Import(string name, byte[] bytes)
    {
        var path = Path.Combine(_paths.Ingest, "wii", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var file = new IngestFile("wii", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        return (await _pipeline.ProcessAsync(file, Guid.NewGuid(), Actor, new CircuitBreaker(), CancellationToken.None))!;
    }

    private InfoJsonGame ReadGame()
    {
        using var zip = LibraryZip.Open(Path.Combine(_paths.Roms, ZipPath), _reader);
        return zip.InfoJson!.Game!;
    }

    private sealed class WiiTestHandler : ISystemHandler
    {
        public required RomHeader Header { get; set; }
        public string Id => "wii";
        public string DisplayName => "Wii";
        public string Family => "nintendo-disc";
        public IReadOnlyList<string> RomExtensions => [".iso", ".wbfs"];
        public long MinRomSizeBytes => 32 * 1024;
        public long MaxRomSizeBytes => 9L * 1024 * 1024 * 1024;
        public string DatName => "Nintendo - Wii";
        public string DatFolder => DatKind.Redump;
        public IReadOnlyList<string> MetadatKinds => [];
        public bool EmbedRomsInZip => false;
        public bool FileSets => true;
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;
        public RomHeader ReadHeader(ReadOnlySpan<byte> rom) => Header;
    }
}
