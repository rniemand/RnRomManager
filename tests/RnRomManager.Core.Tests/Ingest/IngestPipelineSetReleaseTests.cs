using System.Security.Cryptography;
using System.Text;
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
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec B14 step 2: an unmatched bin/cue set from Review stored as one release, all or nothing.</summary>
public class IngestPipelineSetReleaseTests
{
    private const string Cue = "Mystery (Disc 2).cue";
    private const string Track1 = "Mystery (Disc 2) (Track 1).bin";
    private const string Track2 = "Mystery (Disc 2) (Track 2).bin";
    private static readonly AuditActor Actor = new(42, "boss", Guid.NewGuid());

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private string _ingest = null!;
    private IRomRepository _roms = null!;
    private InfoJsonReader _reader = null!;
    private readonly ExternalSetTestHandler _system = new();

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        _ingest = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalSetTestHandler.SystemId)).FullName;
        _roms = Substitute.For<IRomRepository>();
        _roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([]);
        _roms.ListExternalAsync(default).ReturnsForAnyArgs([]);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private IngestPipeline Pipeline(ExternalIngestHooks? hooks = null)
    {
        var systems = new SystemRegistry([_system]);
        var options = Options.Create(_paths);
        var games = Substitute.For<IGameRepository>();
        games.ListZipPathEquivalenceAsync(default!, default).ReturnsForAnyArgs(c =>
            c.Arg<IReadOnlyList<string>>().Select(path => new ZipPathEquivalenceGroup([path], [])).ToList());
        _reader = new InfoJsonReader([], systems);
        return new IngestPipeline(new DatCache(options, systems, NullLogger<DatCache>.Instance), systems, _roms, games, Substitute.For<ILibraryMirror>(),
            Substitute.For<IIntegrityIssueRepository>(), Substitute.For<IIngestReviewRepository>(), _reader, TestMedia.Offline(), options,
            Substitute.For<IAuditLog>(), new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance)
        {
            ExternalHooks = hooks,
        };
    }

    /// <summary>The cue and two tracks in ingest, in set order, each with its SHA1.</summary>
    private List<(IngestFile File, string Sha1)> DropSet()
    {
        var files = new[]
        {
            (Cue, Encoding.UTF8.GetBytes($"FILE \"{Track1}\" BINARY\n  TRACK 01 MODE1/2352\nFILE \"{Track2}\" BINARY\n  TRACK 02 AUDIO\n")),
            (Track1, RandomNumberGenerator.GetBytes(2352)),
            (Track2, RandomNumberGenerator.GetBytes(4704)),
        };
        return files.Select(f =>
        {
            var path = Path.Combine(_ingest, f.Item1);
            File.WriteAllBytes(path, f.Item2);
            return (new IngestFile(ExternalSetTestHandler.SystemId, path, f.Item1, f.Item2.Length, File.GetLastWriteTimeUtc(path)),
                Convert.ToHexStringLower(SHA1.HashData(f.Item2)));
        }).ToList();
    }

    private static InfoJsonRom Rom() => new()
    {
        FileName = "", OriginalFileName = "", BaseTitle = "Mystery", Regions = ["Europe"], Languages = [], Revision = null, Flags = ["Disc 2"],
        IsPrimary = false, Identification = new InfoJsonIdentification(IdentificationMethod.Manual, null, null, null), Metadata = null!,
    };

    private string GameFolder => Path.Combine(_paths.Roms, ExternalSetTestHandler.SystemId, Bucket.For("Mystery.zip"));

    private Task<IngestItemResult> StoreAsync(IngestPipeline pipeline, IReadOnlyList<(IngestFile, string)> members, List<Guid>? mirrored = null,
        JobProgress? progress = null) =>
        pipeline.StoreSetReleaseAsync(members, _system, Rom(), DatMetadata.Empty, null,
            id => { mirrored?.Add(id); return Task.CompletedTask; }, Actor, "Manual", progress ?? new JobProgress());

    private void AssertNothingMoved(IEnumerable<(IngestFile File, string)> members)
    {
        Assert.That(members.All(m => File.Exists(m.File.FullPath)), Is.True, "every member stays in ingest");
        Assert.That(Directory.Exists(GameFolder) ? Directory.GetFiles(GameFolder, "*.bin") : [], Is.Empty);
    }

    [Test]
    public async Task ACueAndTwoTracks_AreOneRelease_NamesKept_Track1Main()
    {
        var members = DropSet();
        var mirrored = new List<Guid>();

        var result = await StoreAsync(Pipeline(), members, mirrored);

        Assert.That((result.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Ingested, (string?)null)), result.Details);
        Assert.That(mirrored, Is.EqualTo(new[] { result.GameId!.Value }));
        Assert.That(Directory.GetFiles(GameFolder).Select(Path.GetFileName).Where(n => !n!.EndsWith(".zip")).Order(),
            Is.EqualTo(new[] { Cue, Track1, Track2 }.Order()));
        var zip = Directory.GetFiles(GameFolder, "*.zip").Single();
        var target = LibraryTarget.Open(_paths.Roms, Path.GetRelativePath(_paths.Roms, zip).Replace('\\', '/'), _reader);
        target.Zip!.Dispose();
        var rom = target.Game!.Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.Metadata.Sha1, rom.BaseTitle), Is.EqualTo((Track1, Track1, members[1].Sha1, "Mystery")));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Disc 2" }));
        Assert.That(rom.Files!.Select(f => (f.ExternalFileName, f.Sha1)), Is.EqualTo(new[] { (Cue, members[0].Sha1), (Track2, members[2].Sha1) }));
        Assert.That(members.Any(m => File.Exists(m.File.FullPath)), Is.False);
    }

    [Test]
    public async Task AMemberWithAnotherSha1_IsVerifyFailed_AndNothingMoves()
    {
        var members = DropSet();
        members[2] = (members[2].File, new string('0', 40));

        var result = await StoreAsync(Pipeline(), members);

        Assert.That(result.Reason, Is.EqualTo(IngestErrors.VerifyFailed));
        AssertNothingMoved(members);
    }

    [Test]
    public async Task AMemberChangedAfterItWasHashed_IsVerifyFailed_AndNothingMoves()
    {
        var members = DropSet();
        var track1 = members[1].File.FullPath;
        var pipeline = Pipeline(new ExternalIngestHooks
        {
            BeforeHash = file =>
            {
                if (file.Name == Track2)
                    File.AppendAllText(track1, "more"); // Track 1 was hashed already
            },
            Move = new ExternalMoveHooks { Rename = (_, _) => throw new AssertionException("nothing may move") },
        });

        var result = await StoreAsync(pipeline, members);

        Assert.That(result.Reason, Is.EqualTo(IngestErrors.VerifyFailed));
        AssertNothingMoved(members);
    }

    [Test]
    public async Task AFailingSecondMove_RollsTheFirstBack()
    {
        var members = DropSet();
        var pipeline = Pipeline(new ExternalIngestHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => Path.GetFileName(to).StartsWith(Track1, StringComparison.Ordinal)
                    ? new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused")
                    : NoReplaceRename.Move(from, to),
            },
        });

        var result = await StoreAsync(pipeline, members);

        Assert.That(result.Outcome, Is.EqualTo(IngestOutcome.Error));
        AssertNothingMoved(members);
        Assert.That(Directory.Exists(GameFolder) ? Directory.GetFiles(GameFolder) : [], Is.Empty);
    }

    [Test]
    public async Task AMemberNameAlreadyInTheGamesFolder_IsNameTaken_AndNothingMoves()
    {
        var members = DropSet();
        Directory.CreateDirectory(GameFolder);
        File.WriteAllBytes(Path.Combine(GameFolder, Track2), [9]);

        var result = await StoreAsync(Pipeline(), members);

        Assert.That(result.Reason, Is.EqualTo(IngestErrors.NameTaken));
        Assert.That(members.All(m => File.Exists(m.File.FullPath)), Is.True);
    }

    [Test]
    public async Task AMainFileAlreadyInTheLibrary_IsDuplicate_AndNothingIsHashed()
    {
        var members = DropSet();
        _roms.GetBySha1Async(members[1].Sha1, Arg.Any<CancellationToken>()).Returns(new RomRow
        {
            GameId = Guid.NewGuid(), SystemId = ExternalSetTestHandler.SystemId, FileName = Track1, OriginalFileName = Track1, Sha1 = members[1].Sha1,
            Crc32 = "00000000", Md5 = new string('0', 32), SizeBytes = 1, BaseTitle = "Mystery", Regions = [], Languages = [], Flags = [],
            RegionKey = "", IdMethod = IdentificationMethod.Manual, DateAdded = default, DateModified = default,
        });
        var pipeline = Pipeline(new ExternalIngestHooks { BeforeHash = _ => throw new AssertionException("nothing may be hashed") });

        var result = await StoreAsync(pipeline, members);

        Assert.That(result.Reason, Is.EqualTo("Duplicate"));
        AssertNothingMoved(members);
    }

    [Test]
    public async Task EachMember_IsAnItem_WithItsOwnHashingStep()
    {
        var members = DropSet();
        var progress = new JobProgress();
        var seen = new List<(string?, string?)>();
        var pipeline = Pipeline(new ExternalIngestHooks
        {
            WrapHashStream = stream =>
            {
                seen.Add((progress.CurrentItem, progress.StepAt(DateTime.UtcNow)?.Phase));
                return stream;
            },
        });

        await StoreAsync(pipeline, members, progress: progress);

        Assert.That(seen, Is.EqualTo(new[] { (Cue, JobStepPhases.Hashing), (Track1, JobStepPhases.Hashing), (Track2, JobStepPhases.Hashing) }));
        Assert.That(progress.Current, Is.EqualTo(3));
    }
}
