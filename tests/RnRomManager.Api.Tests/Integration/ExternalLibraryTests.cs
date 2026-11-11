using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// Spec I4, I5, I7: a new external ROM is moved next to its game's ZIP right before the ZIP write, and every failure
/// after the move puts it back (or keeps it, when its origin can't be trusted any more).
/// </summary>
[Category("Integration")]
public partial class ExternalLibraryTests
{
    private const string GameName = "Space Game (USA)";
    private const string ZipRelative = "ext/S/Space Game.zip";
    private const string EuropeName = "Space Game (Europe)";
    private static readonly byte[] Rom = RandomNumberGenerator.GetBytes(4096);
    private static readonly byte[] Europe = RandomNumberGenerator.GetBytes(2048);

    private ApiFactory _factory = null!;
    private List<JobItem> _items = null!;
    private IJobItemRepository _itemRepository = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>(services),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = ""; // default art, no network
        _ = _factory.Services; // start the host: it creates the system folders
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [ExternalTestHandler.DatNameValue] =
            [
                new FixtureGame(GameName, GameName + ".nsp", Rom),
                new FixtureGame(EuropeName, EuropeName + ".nsp", Europe),
            ],
            [GbDat] = [new FixtureGame("Shared (World)", "Shared (World).gb", SharedGb), new FixtureGame("Mystery GB (World)", "Mystery GB (World).gb", Mystery)],
        });
        cache.Load();
        _items = [];
        _itemRepository = Substitute.For<IJobItemRepository>();
        _itemRepository.InsertAsync(Arg.Do<JobItem>(_items.Add), Arg.Any<CancellationToken>()).Returns(1L);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private string Ingest => Path.Combine(_factory.Root, "ingest", ExternalTestHandler.SystemId);
    private string Bucket => Path.Combine(_factory.Root, "roms", "ext", "S");
    private string ZipPath => Path.Combine(Bucket, "Space Game.zip");
    private string Sidecar => Path.Combine(Bucket, $"Space Game.{GameName}.xci");

    /// <summary>A loose file a minute old; the listing carries the mtime the filesystem actually stored.</summary>
    private IngestFile Drop(string name, byte[]? bytes = null)
    {
        var path = Path.Combine(Ingest, name);
        File.WriteAllBytes(path, bytes ?? Rom);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var info = new FileInfo(path);
        return new IngestFile(ExternalTestHandler.SystemId, path, name, info.Length, info.LastWriteTimeUtc);
    }

    private IngestPipeline Pipeline(ExternalIngestHooks? hooks = null, ILibraryMirror? mirror = null, IAuditLog? audit = null,
        IIngestReviewRepository? reviews = null)
    {
        object[] overrides = [.. new object?[] { mirror, audit, reviews }.OfType<object>()];
        var pipeline = overrides.Length == 0 ? Service<IngestPipeline>() : ActivatorUtilities.CreateInstance<IngestPipeline>(_factory.Services, overrides);
        pipeline.ExternalHooks = hooks;
        return pipeline;
    }

    private static Task<IngestItemResult?> IngestAsync(IngestPipeline pipeline, IngestFile file) =>
        pipeline.ProcessAsync(file, Guid.NewGuid(), new AuditActor(null, null, Guid.NewGuid()), new CircuitBreaker(), CancellationToken.None);

    private static (string?, string?) Code(IngestItemResult? result) => (result?.Outcome, result?.Reason);

    private string[] LibraryFiles() =>
        Directory.Exists(Bucket) ? [.. Directory.GetFiles(Bucket).Select(Path.GetFileName).Order()!] : [];

    /// <summary>Makes the ZIP write fail verification: its temp is rewritten in place with an unreadable <c>info.json</c>.</summary>
    private LibraryZipWriterHooks FailVerify(Action? alsoAfterTemp = null, string? zipPath = null) => new()
    {
        AfterTempClosed = () =>
        {
            using (var temp = new FileStream((zipPath ?? ZipPath) + ".tmp", FileMode.Open, FileAccess.ReadWrite))
            {
                temp.SetLength(0);
                using var zip = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true);
                using var info = new StreamWriter(zip.CreateEntry(LibraryZip.InfoJsonEntry).Open());
                info.Write("not json");
            }
            alsoAfterTemp?.Invoke();
        },
    };

    private async Task<List<(string Action, string? Details)>> AuditAsync()
    {
        await Service<RnRomManager.Api.AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. await db.QueryAsync<(string, string?)>("SELECT action, details FROM audit_events ORDER BY id")];
    }

    [Test]
    public async Task NewGame_TheFileLandsBesideItsZip_UnderTheSourceExtension()
    {
        var file = Drop("dump.XCI");

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(File.Exists(file.FullPath), Is.False, "the source was moved, not copied");
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        using (var zip = ZipFile.OpenRead(ZipPath))
            Assert.That(zip.Entries.Select(e => e.FullName), Has.None.StartsWith(LibraryZip.RomsFolder));
        using (var library = LibraryZip.Open(ZipPath, Service<InfoJsonReader>()))
        {
            var rom = library.InfoJson!.Game!.Roms.Single();
            Assert.That((rom.FileName, rom.ExternalFileName), Is.EqualTo((GameName + ".xci", Path.GetFileName(Sidecar))));
        }
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<string>("SELECT external_file_name FROM roms"), Is.EqualTo(Path.GetFileName(Sidecar)));
        Assert.That(await db.QuerySingleAsync<string>("SELECT zip_path FROM games"), Is.EqualTo(ZipRelative));
    }

    [Test]
    public async Task VerifyFailure_PutsTheFileBack_AndLeavesNothingInTheLibrary()
    {
        var file = Drop("dump.xci");

        var result = await IngestAsync(Pipeline(new ExternalIngestHooks { Writer = FailVerify() }), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.VerifyFailed)));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
        Assert.That(LibraryFiles(), Is.Empty);
    }

    [Test]
    public async Task MirrorFailure_KeepsTheFileAtItsLibraryName_AndTheAuditRowCameFirst()
    {
        var mirror = Substitute.For<ILibraryMirror>();
        mirror.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));
        var file = Drop("dump.xci");

        var result = await IngestAsync(Pipeline(mirror: mirror), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.DbWriteFailed)));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(await AuditAsync(), Is.EqualTo(new[] { (AuditActions.Ingested, (string?)$"release {GameName}.xci") }));
    }

    [Test]
    public async Task DestinationGoneAfterAllocation_IsAnIoError_AndTheSourceIsUntouched()
    {
        var file = Drop("dump.xci");
        var hooks = new ExternalIngestHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) =>
                {
                    if (Directory.Exists(Bucket))
                        Directory.Delete(Bucket);
                    return NoReplaceRename.Move(from, to);
                },
            },
        };

        var result = await IngestAsync(Pipeline(hooks), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
        Assert.That(Directory.GetFiles(Path.Combine(_factory.Root, "roms"), "*.tmp", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task CrossVolumeCopy_WithTheRightLengthButAnotherSha1_IsTooRecent()
    {
        var file = Drop("dump.xci");
        var hooks = new ExternalIngestHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => from.EndsWith("dump.xci", StringComparison.Ordinal) ? new NoReplaceRenameResult(NoReplaceRenameStatus.CrossDevice) : NoReplaceRename.Move(from, to),
                WrapSource = source =>
                {
                    var bytes = new byte[Rom.Length];
                    source.ReadExactly(bytes);
                    bytes[0] ^= 0xFF;
                    return new MemoryStream(bytes);
                },
            },
        };

        var result = await IngestAsync(Pipeline(hooks), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Skipped, IngestReason.TooRecent)));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
        Assert.That(LibraryFiles(), Is.Empty);
    }

    [Test]
    public async Task OccupiedTemp_ForcesASuffix_AndStaysIntact()
    {
        Directory.CreateDirectory(Bucket);
        var occupant = Sidecar + ".tmp";
        File.WriteAllText(occupant, "someone else's");

        var result = await IngestAsync(Pipeline(), Drop("dump.xci"));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, $"Space Game.{GameName} (2).xci")), Is.EqualTo(Rom));
        Assert.That(File.ReadAllText(occupant), Is.EqualTo("someone else's"));
    }

    [Test]
    public async Task SourceModifiedAfterHashing_IsSkippedBeforeStaging()
    {
        var file = Drop("dump.xci");
        var hooks = new ExternalIngestHooks { BeforePlacement = f => File.AppendAllText(f.FullPath, "more") };

        var result = await IngestAsync(Pipeline(hooks), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Skipped, IngestReason.TooRecent)));
        Assert.That(new FileInfo(file.FullPath).Length, Is.EqualTo(Rom.Length + 4));
        Assert.That(LibraryFiles(), Is.Empty);
    }

    [Test]
    public async Task StagedSourceChangedSinceHashing_IsRestored_AndSkipped()
    {
        var file = Drop("dump.xci");
        var hooks = new ExternalIngestHooks { Move = new ExternalMoveHooks { AfterStaging = staging => File.AppendAllText(staging, "more") } };

        var result = await IngestAsync(Pipeline(hooks), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Skipped, IngestReason.TooRecent)));
        Assert.That(new FileInfo(file.FullPath).Length, Is.EqualTo(Rom.Length + 4), "back at its ingest path");
        Assert.That(LibraryFiles(), Is.Empty);
    }

    [Test]
    public async Task SidecarReplacedBeforeRollback_ByASameSizeFile_IsPreserved()
    {
        var file = Drop("dump.xci");
        var replacement = RandomNumberGenerator.GetBytes(Rom.Length);
        var hooks = new ExternalIngestHooks
        {
            Writer = new LibraryZipWriterHooks
            {
                AfterTempClosed = () =>
                {
                    File.Delete(Sidecar);
                    File.WriteAllBytes(Sidecar, replacement);
                },
            },
        };

        var result = await IngestAsync(Pipeline(hooks), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.VerifyFailed)));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(replacement), "not moved into ingest as if it were the source");
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(result!.Details, Does.Contain("replaced"));
    }

    private string Archive(string name, byte[] rom)
    {
        var path = Path.Combine(Ingest, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("dump.nsp").Open();
            stream.Write(rom);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    private IngestJob Job(IngestPipeline pipeline) =>
        ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, _itemRepository, pipeline);

    [TestCase(false, TestName = "ArchiveEntry_ZipWriteFails_TheCopyIsDeleted_WhileTheArchiveStillMatches")]
    [TestCase(true, TestName = "ArchiveEntry_ZipWriteFails_TheCopyIsKept_WhenTheArchiveWasReplaced")]
    public async Task ArchiveEntry_ZipWriteFails(bool replaceArchive)
    {
        var archive = Archive("pack.zip", Rom);
        var writer = FailVerify(() =>
        {
            if (!replaceArchive)
                return;
            File.Delete(archive);
            Archive("pack.zip", RandomNumberGenerator.GetBytes(16));
        });
        var orphan = Path.Combine(Bucket, $"Space Game.{GameName}.nsp");

        await Job(Pipeline(new ExternalIngestHooks { Writer = writer })).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        var item = _items.Single(i => i.ArchiveEntry == "dump.nsp");
        Assert.That((item.Outcome, item.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.VerifyFailed)));
        Assert.That(File.Exists(archive), Is.True, "an Error keeps the archive");
        if (replaceArchive)
        {
            Assert.That(File.ReadAllBytes(orphan), Is.EqualTo(Rom), "the only verified copy");
            Assert.That(item.Details, Does.Contain(orphan));
        }
        else
        {
            Assert.That(LibraryFiles(), Is.Empty);
        }
    }

    [Test]
    public async Task CancelDuringTheZipWrite_TheStartedItemStillCompletes()
    {
        using var cancel = new CancellationTokenSource();
        var first = Drop("a.xci");
        var second = Drop("b.xci", RandomNumberGenerator.GetBytes(64));
        var pipeline = Pipeline(new ExternalIngestHooks { Writer = new LibraryZipWriterHooks { AfterTempClosed = cancel.Cancel } });

        Assert.That(async () => await Job(pipeline).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), cancel.Token),
            Throws.InstanceOf<OperationCanceledException>());

        Assert.That(_items.Select(i => (i.SourcePath, i.Outcome)), Is.EqualTo(new[] { (first.FullPath, IngestOutcome.Ingested) }));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.Exists(second.FullPath), Is.True, "not started");
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM roms"), Is.EqualTo(1));
    }

    private string EuropeSidecar => Path.Combine(Bucket, $"Space Game.{EuropeName}.nsp");

    /// <summary>The first release, ingested normally: the game the second release joins.</summary>
    private async Task<Guid> FirstReleaseAsync()
    {
        var first = await IngestAsync(Pipeline(), Drop("dump.xci"));
        Assert.That(Code(first), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        return first!.GameId!.Value;
    }

    private InfoJsonGame ReadGame()
    {
        using var zip = LibraryZip.Open(ZipPath, Service<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private async Task<int> RomRowsAsync()
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM roms");
    }

    [Test]
    public async Task SecondRelease_LandsBesideTheOriginalZip()
    {
        var gameId = await FirstReleaseAsync();
        var file = Drop("eu.nsp", Europe);

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(result!.GameId, Is.EqualTo(gameId));
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom), "the first release is untouched");
        Assert.That(ReadGame().Roms.Select(r => r.ExternalFileName), Is.EquivalentTo(new[] { Path.GetFileName(Sidecar), Path.GetFileName(EuropeSidecar) }));
        Assert.That(await RomRowsAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task SecondRelease_WithAnOccupiedTemp_GetsASuffix()
    {
        await FirstReleaseAsync();
        var occupant = EuropeSidecar + ".tmp";
        File.WriteAllText(occupant, "someone else's");

        var result = await IngestAsync(Pipeline(), Drop("eu.nsp", Europe));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, $"Space Game.{EuropeName} (2).nsp")), Is.EqualTo(Europe));
        Assert.That(File.ReadAllText(occupant), Is.EqualTo("someone else's"));
    }

    [TestCase("game", TestName = "SecondRelease_StaleOrUnreadableTarget_LeavesTheSource stale game id")]
    [TestCase("system", TestName = "SecondRelease_StaleOrUnreadableTarget_LeavesTheSource stale system")]
    [TestCase("unreadable", TestName = "SecondRelease_StaleOrUnreadableTarget_LeavesTheSource unreadable info.json")]
    public async Task SecondRelease_StaleOrUnreadableTarget_LeavesTheSource(string damage)
    {
        await FirstReleaseAsync();
        var game = ReadGame();
        var info = damage switch
        {
            "game" => InfoJsonSerializer.Serialize(game with { GameId = Guid.NewGuid() }),
            // The only other registered systems are embedded, where the pointer is invalid: as unusable as a stale system.
            "system" => InfoJsonSerializer.Serialize(game with { System = "gb" }),
            _ => "not json"u8.ToArray(),
        };
        using (var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Update))
        {
            zip.GetEntry(LibraryZip.InfoJsonEntry)!.Delete();
            using var stream = zip.CreateEntry(LibraryZip.InfoJsonEntry).Open();
            stream.Write(info);
        }
        var file = Drop("eu.nsp", Europe);

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.TargetZipUnreadable)));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Europe));
        Assert.That(File.Exists(EuropeSidecar), Is.False);
    }

    [Test]
    public async Task SecondRelease_MirrorFailure_KeepsTheReleaseAndItsPendingRow_AndTheNextSyncPicksItUp()
    {
        await FirstReleaseAsync();
        var file = Drop("eu.nsp", Europe);
        var reviews = Service<IIngestReviewRepository>();
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(Europe));
        await reviews.UpsertAsync(new ReviewItem
        {
            SystemId = ExternalTestHandler.SystemId, Reason = "NoDatMatch", SourcePath = file.FullPath, OriginalFileName = file.Name,
            Sha1 = sha1, Md5 = new string('0', 32), Crc32 = "00000000", SizeBytes = Europe.Length, CreatedAt = DateTime.UtcNow,
            LastSeenJobId = Guid.NewGuid(),
        }, default);
        var mirror = Substitute.For<ILibraryMirror>();
        mirror.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await IngestAsync(Pipeline(mirror: mirror), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.DbWriteFailed)));
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
        Assert.That(ReadGame().Roms, Has.Count.EqualTo(2), "the release is committed in the ZIP");
        Assert.That((await reviews.GetBySha1Async(sha1, default))?.Status, Is.EqualTo("Pending"), "kept for a retry");
        Assert.That(await RomRowsAsync(), Is.EqualTo(1));

        await Service<DbSync>().SyncAllAsync(default);

        Assert.That(await RomRowsAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task SecondRelease_ZipReplacedRightAfterPublication_IsNotMirrored()
    {
        await FirstReleaseAsync();
        var audit = Substitute.For<IAuditLog>();
        audit.When(a => a.Record(Arg.Is<AuditEvent>(e => e.Action == AuditActions.Ingested))).Do(_ =>
        {
            // A regular file with the same content, but not the one the write published.
            File.Copy(ZipPath, ZipPath + ".copy");
            File.Delete(ZipPath);
            File.Move(ZipPath + ".copy", ZipPath);
        });

        var result = await IngestAsync(Pipeline(audit: audit), Drop("eu.nsp", Europe));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.DbWriteFailed)));
        Assert.That(await RomRowsAsync(), Is.EqualTo(1), "the mirror was not updated");
        Assert.That(File.Exists(EuropeSidecar), Is.True);
    }
}
