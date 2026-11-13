using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec B12: a Review item's unmatched bin/cue set, planned from the system's ingest folder as ingest plans it.</summary>
public class ReviewSetsTests
{
    private const string Cue = "Game (Disc 2).cue";
    private const string Track1 = "Game (Disc 2) (Track 1).bin";
    private const string Track2 = "Game (Disc 2) (Track 2).bin";

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private string _folder = null!;
    private IIngestReviewRepository _reviews = null!;
    private Dictionary<string, ReviewItem> _rows = null!;
    private ReviewSets _sets = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        _folder = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalSetTestHandler.SystemId)).FullName;
        _rows = new Dictionary<string, ReviewItem>(StringComparer.Ordinal);
        _reviews = Substitute.For<IIngestReviewRepository>();
        _reviews.ListPendingByPathsAsync(default!, default).ReturnsForAnyArgs(c =>
            c.Arg<IReadOnlyCollection<string>>().Where(_rows.ContainsKey).Select(p => _rows[p]).ToList());
        var systems = new SystemRegistry([new ExternalSetTestHandler(), new ExternalTestHandler()]);
        var options = Options.Create(_paths);
        _sets = new ReviewSets(new IngestFileScanner(options, systems, NullLogger<IngestFileScanner>.Instance), _reviews, systems, options,
            NullLogger<ReviewSets>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Drops a cue naming both tracks, and the tracks; every file gets a Pending NoDatMatch row.</summary>
    private void DropSet(bool withRows = true)
    {
        Write(Cue, Encoding.UTF8.GetBytes($"FILE \"{Track1}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
            $"FILE \"{Track2}\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n"));
        Write(Track1, [1, 2, 3]);
        Write(Track2, [4, 5]);
        if (withRows)
            foreach (var (name, id) in new[] { (Cue, 1L), (Track1, 2L), (Track2, 3L) })
                Row(name, id);
    }

    private ReviewItem Row(string name, long id, string reason = "NoDatMatch")
    {
        var path = Path.Combine(_folder, name);
        return _rows[path] = new ReviewItem
        {
            Id = id, SystemId = ExternalSetTestHandler.SystemId, Status = "Pending", Reason = reason, SourcePath = path,
            OriginalFileName = name, SizeBytes = new FileInfo(path).Length, Sha1 = new string((char)('a' + id), 40),
        };
    }

    [TestCase(Cue)]
    [TestCase(Track2)]
    public async Task CompleteUnmatchedSet_IsFound_InSetOrder_FromAnyMember(string from)
    {
        DropSet();

        var set = await _sets.FindAsync(_rows[Path.Combine(_folder, from)]);

        Assert.That(set!.Select(m => (m.File.Name, m.Row.Id)), Is.EqualTo(new[] { (Cue, 1L), (Track1, 2L), (Track2, 3L) }));
    }

    [Test]
    public async Task MissingBin_IsNoSet()
    {
        DropSet();
        File.Delete(Path.Combine(_folder, Track2));

        Assert.That(await _sets.FindAsync(_rows[Path.Combine(_folder, Cue)]), Is.Null);
    }

    [Test]
    public async Task AResolvedMember_OrOneWithNoRow_IsNoSet()
    {
        DropSet();
        var cue = _rows[Path.Combine(_folder, Cue)];
        _rows.Remove(Path.Combine(_folder, Track1)); // Resolved, or skipped as AlreadyInReview: no Pending row

        Assert.That(await _sets.FindAsync(cue), Is.Null);
    }

    [Test]
    public async Task AnAmbiguousSetMember_IsNoSet()
    {
        DropSet();
        Row(Track1, 2, ReviewReasons.AmbiguousSet);

        Assert.That(await _sets.FindAsync(_rows[Path.Combine(_folder, Cue)]), Is.Null);
    }

    [Test]
    public async Task AnArchivedItem_IsNoSet()
    {
        DropSet();
        var cue = _rows[Path.Combine(_folder, Cue)] with { ArchivePath = Path.Combine(_folder, "pack.zip"), ArchiveEntry = Cue };

        Assert.That(await _sets.FindAsync(cue), Is.Null);
        await _reviews.DidNotReceiveWithAnyArgs().ListPendingByPathsAsync(default!, default);
    }

    [Test]
    public async Task ALoneBin_IsNoSet()
    {
        Write("Lone.bin", [1]);

        Assert.That(await _sets.FindAsync(Row("Lone.bin", 9)), Is.Null);
    }

    [Test]
    public async Task AnUnreadableCue_IsNoSet()
    {
        DropSet();
        // A cue over the planner's size limit isn't read, so the tracks are lone files.
        Write(Cue, new byte[IngestSetPlanner.MaxCueBytes + 1]);

        Assert.That(await _sets.FindAsync(_rows[Path.Combine(_folder, Track1)]), Is.Null);
    }

    [Test]
    public async Task AnotherSystem_IsNoSet()
    {
        var item = new ReviewItem
        {
            Id = 1, SystemId = ExternalTestHandler.SystemId, Status = "Pending", Reason = "NoDatMatch",
            SourcePath = Path.Combine(_paths.Ingest, ExternalTestHandler.SystemId, "x.cue"), OriginalFileName = "x.cue", Sha1 = new string('a', 40),
        };

        Assert.That(await _sets.FindAsync(item), Is.Null);
    }
}
