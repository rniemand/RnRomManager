using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec I3 dedupe and I6 self-repair for external ROMs.</summary>
public partial class ExternalLibraryTests
{
    private const string GbDat = "Nintendo - Game Boy";
    private static readonly byte[] SharedGb = TestLibrary.RomBytes(9);

    private string Pointer => Path.GetFileName(Sidecar);
    private string Backup => Sidecar + ExternalRomName.RepairBackupSuffix;
    private string Duplicates(string name) => Path.Combine(Ingest, "_duplicates", name);

    private async Task<long> InsertIssueAsync(string type, string zipPath, string entryName, Guid? gameId)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.ExecuteScalarAsync<long>("""
            INSERT INTO integrity_issues (type, severity, system_id, zip_path, game_id, entry_name, status, first_seen_job_id, last_seen_job_id, detected_at)
            VALUES (@type, 'Error', 'ext', @zipPath, @gameId, @entryName, 'Open', @job, @job, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """, new { type, zipPath, entryName, gameId = gameId?.ToString(), job = Guid.NewGuid().ToString() });
    }

    private async Task<string> IssueStatusAsync(long id)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QuerySingleAsync<string>("SELECT status FROM integrity_issues WHERE id = @id", new { id });
    }

    /// <summary>The first release with a damaged file at its pointer, and an open issue on it.</summary>
    /// <param name="wrongLength">Bytes left at the pointer (wrong length); null deletes the file.</param>
    private async Task<(Guid GameId, long IssueId)> DamagedAsync(byte[]? wrongLength, string? spelling = null)
    {
        var gameId = await FirstReleaseAsync();
        File.Delete(Sidecar);
        if (wrongLength is not null)
            File.WriteAllBytes(Path.Combine(Bucket, spelling ?? Pointer), wrongLength);
        var issue = await InsertIssueAsync(wrongLength is null ? IntegrityIssueType.EntryMissing : IntegrityIssueType.EntrySizeMismatch,
            ZipRelative, Pointer, gameId);
        return (gameId, issue);
    }

    private static readonly byte[] Short = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    [TestCase("pointer", TestName = "IssueOnAnotherFile_IsIgnored_AndTheDuplicateNamesTheExternalFile accent-distinct pointer")]
    [TestCase("zip", TestName = "IssueOnAnotherFile_IsIgnored_AndTheDuplicateNamesTheExternalFile accent-distinct ZIP path")]
    [TestCase("game", TestName = "IssueOnAnotherFile_IsIgnored_AndTheDuplicateNamesTheExternalFile another game id")]
    [TestCase("none", TestName = "IssueOnAnotherFile_IsIgnored_AndTheDuplicateNamesTheExternalFile no issue")]
    public async Task IssueOnAnotherFile_IsIgnored_AndTheDuplicateNamesTheExternalFile(string other)
    {
        var gameId = await FirstReleaseAsync();
        long? issue = other switch
        {
            "pointer" => await InsertIssueAsync(IntegrityIssueType.EntryMissing, ZipRelative, Pointer.Replace("(USA)", "(USÁ)"), gameId),
            "zip" => await InsertIssueAsync(IntegrityIssueType.EntryMissing, ZipRelative.Replace("Game.zip", "Gamé.zip"), Pointer, gameId),
            "game" => await InsertIssueAsync(IntegrityIssueType.EntryMissing, ZipRelative, Pointer, Guid.NewGuid()),
            _ => null,
        };
        var file = Drop("again.xci");

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Duplicate, (string?)null)));
        Assert.That(result!.Details, Is.EqualTo($"same as {ZipRelative} › {Pointer}"));
        Assert.That(File.ReadAllBytes(Duplicates("again.xci")), Is.EqualTo(Rom), "moved to _duplicates/ only");
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom), "the library file is untouched");
        if (issue is { } id)
            Assert.That(await IssueStatusAsync(id), Is.EqualTo("Open"));
    }

    [Test]
    public async Task ArchiveDuplicate_FollowsTheArchiveDisposition()
    {
        await FirstReleaseAsync();
        var archive = Archive("pack.zip", Rom);

        await Job(Pipeline()).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        Assert.That(_items.Single(i => i.ArchiveEntry == "dump.nsp").Outcome, Is.EqualTo(IngestOutcome.Duplicate));
        Assert.That(File.Exists(archive), Is.False);
        Assert.That(File.Exists(Duplicates("pack.zip")), Is.True);
    }

    [Test]
    public async Task SameSha1AcrossStorageModes_WithAnOpenRepairIssue_StaysADuplicate()
    {
        var gb = Path.Combine(_factory.Root, "ingest", "gb", "shared.gb");
        File.WriteAllBytes(gb, SharedGb);
        var embedded = await IngestAsync(Pipeline(), new IngestFile("gb", gb, "shared.gb", SharedGb.Length, DateTime.UtcNow.AddMinutes(-1)));
        Assert.That(Code(embedded), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var zip = Path.Combine(_factory.Root, "roms", "gb", "S", "Shared.zip");
        var before = File.ReadAllBytes(zip);
        var issue = await InsertIssueAsync(IntegrityIssueType.HashMismatch, "gb/S/Shared.zip", LibraryZip.RomEntry("Shared (World).gb"), embedded!.GameId);

        var result = await IngestAsync(Pipeline(), Drop("shared.nsp", SharedGb));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Duplicate, (string?)null)));
        Assert.That(File.ReadAllBytes(zip), Is.EqualTo(before));
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo("Open"));
    }

    [Test]
    public async Task DeletedFile_IsRepaired_WithoutARomsEntry()
    {
        var (_, issue) = await DamagedAsync(wrongLength: null);
        var file = Drop("again.xci");

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Repaired, (string?)null)));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.Exists(file.FullPath), Is.False);
        using (var zip = ZipFile.OpenRead(ZipPath))
            Assert.That(zip.Entries.Select(e => e.FullName), Has.None.StartsWith(LibraryZip.RomsFolder));
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo("Resolved"));
        Assert.That((await AuditAsync()).Last(), Is.EqualTo((AuditActions.ZipRepaired, (string?)null)));
    }

    [TestCase(false, false, TestName = "WrongLengthFile_IsReplaced")]
    [TestCase(true, false, TestName = "WrongLengthFile_ForcedZipFailure_RestoresBothFiles")]
    [TestCase(false, true, TestName = "WrongLengthFile_HardLinkFallback_IsReplaced")]
    [TestCase(true, true, TestName = "WrongLengthFile_HardLinkFallback_ForcedZipFailure_RestoresBothFiles")]
    public async Task WrongLengthFile(bool failWrite, bool useHardLinks)
    {
        var (_, issue) = await DamagedAsync(Short);
        var file = Drop("again.xci");
        var original = ExternalRomPath.ClassifyFile(_factory.Root, Sidecar).Snapshot;
        var hooks = new ExternalIngestHooks
        {
            RepairRename = useHardLinks ? UnsupportedRepairRename : null,
            Writer = failWrite ? FailVerify(() =>
                Assert.That(ExternalRomPath.ClassifyFile(_factory.Root, Backup).Snapshot, Is.EqualTo(original))) : null,
        };

        var result = await IngestAsync(Pipeline(hooks), file);

        if (failWrite)
        {
            Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.VerifyFailed)));
            Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom), "the incoming file is back in ingest");
            Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Short), "the prior library bytes are back");
            Assert.That(ExternalRomPath.ClassifyFile(_factory.Root, Sidecar).Snapshot, Is.EqualTo(original));
        }
        else
        {
            Assert.That(Code(result), Is.EqualTo((IngestOutcome.Repaired, (string?)null)));
            Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        }
        Assert.That(File.Exists(Backup), Is.False);
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo(failWrite ? "Open" : "Resolved"));
    }

    private static NoReplaceRenameResult UnsupportedRepairRename(string from, string to) => NoReplaceRename.FromErrno(95, from, to);

    [TestCase(false)]
    [TestCase(true)]
    public async Task RepairHardLinkCollision_PreservesTheOccupantAndTheOriginal(bool duringRollback)
    {
        await DamagedAsync(Short);
        var file = Drop("again.xci");
        var occupied = duringRollback ? Sidecar : Backup;
        var hooks = new ExternalIngestHooks
        {
            RepairRename = (from, to) =>
            {
                if (to == occupied)
                    File.WriteAllText(to, "late occupant");
                return UnsupportedRepairRename(from, to);
            },
            Writer = duringRollback ? FailVerify() : null,
        };

        var result = await IngestAsync(Pipeline(hooks), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, duringRollback ? IngestErrors.VerifyFailed : IngestErrors.IoError)));
        var survivor = duringRollback ? Backup : Sidecar;
        Assert.That(result!.Details, Does.Contain($"the original file was kept at {survivor}"));
        Assert.That(File.ReadAllText(occupied), Is.EqualTo("late occupant"));
        Assert.That(File.ReadAllBytes(survivor), Is.EqualTo(Short));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
    }

    [Test]
    public async Task RepairHardLinkFallback_MirrorFailure_KeepsTheOriginalBackup()
    {
        var (_, issue) = await DamagedAsync(Short);
        var mirror = Substitute.For<ILibraryMirror>();
        mirror.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));
        var hooks = new ExternalIngestHooks { RepairRename = UnsupportedRepairRename };

        var result = await IngestAsync(Pipeline(hooks, mirror), Drop("again.xci"));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.DbWriteFailed)));
        Assert.That(result!.Details, Does.Contain($"the original file was kept at {Backup}"));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(Backup), Is.EqualTo(Short));
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo("Open"));
    }

    [Test]
    public async Task RepairHardLinkFallback_IfTheOriginalNameCannotBeRemoved_KeepsBothLinks()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Windows sharing modes can prevent the unlink while permitting a hard link.");
        await DamagedAsync(Short);
        var file = Drop("again.xci");
        using var read = File.Open(Sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var result = await IngestAsync(Pipeline(new ExternalIngestHooks { RepairRename = UnsupportedRepairRename }), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
        Assert.That(result!.Details, Does.Contain("couldn't be removed after linking it").And.Contain(Backup));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Short));
        Assert.That(File.ReadAllBytes(Backup), Is.EqualTo(Short));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
    }

    [TestCase(false, TestName = "MisCasedFile_IsRepaired")]
    [TestCase(true, TestName = "MisCasedFile_RollbackRestoresItsActualSpelling")]
    public async Task MisCasedFile(bool failWrite)
    {
        var spelling = Pointer.ToLowerInvariant();
        await DamagedAsync(Short, spelling);
        var file = Drop("again.xci");

        var result = await IngestAsync(Pipeline(failWrite ? new ExternalIngestHooks { Writer = FailVerify() } : null), file);

        var names = LibraryFiles().Where(n => !n.EndsWith(".zip", StringComparison.Ordinal)).ToList();
        if (failWrite)
        {
            Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.VerifyFailed)));
            Assert.That(names, Is.EqualTo(new[] { spelling }));
            Assert.That(File.ReadAllBytes(Path.Combine(Bucket, spelling)), Is.EqualTo(Short));
        }
        else
        {
            Assert.That(Code(result), Is.EqualTo((IngestOutcome.Repaired, (string?)null)));
            Assert.That(names, Is.EqualTo(new[] { Pointer }));
            Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        }
    }

    [Test]
    public async Task TwoCaseVariants_PreventTheRepair()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("NTFS folders hold one spelling of a name.");
        await DamagedAsync(Short);
        File.WriteAllBytes(Path.Combine(Bucket, Pointer.ToUpperInvariant()), Short);
        var file = Drop("again.xci");

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Short));
    }

    [Test]
    public async Task PointerThatDisagreesWithTheMirror_LeavesBothFilesUntouched()
    {
        var gameId = await FirstReleaseAsync();
        const string stale = "Space Game.Other.xci";
        await using (var db = Service<IDbConnectionFactory>().Create())
            await db.ExecuteAsync("UPDATE roms SET external_file_name = @stale", new { stale });
        await InsertIssueAsync(IntegrityIssueType.EntryMissing, ZipRelative, stale, gameId);
        var file = Drop("again.xci");

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.TargetZipUnreadable)));
        Assert.That(result!.Details, Does.Contain("stale"));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
    }

    [Test]
    public async Task ReplacementAtTheBackupPath_SurvivesCleanup()
    {
        await DamagedAsync(Short);
        var hooks = new ExternalIngestHooks
        {
            Writer = new LibraryZipWriterHooks
            {
                AfterTempClosed = () =>
                {
                    File.Delete(Backup);
                    File.WriteAllText(Backup, "someone else's");
                },
            },
        };

        var result = await IngestAsync(Pipeline(hooks), Drop("again.xci"));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Repaired, (string?)null)));
        Assert.That(File.ReadAllText(Backup), Is.EqualTo("someone else's"));
    }

    [TestCase(false, TestName = "ArchiveReplacedBeforeAFailedRepair_KeepsTheIncomingCopyAside_AndRestoresTheOriginal")]
    [TestCase(true, TestName = "ArchiveReplacedBeforeAFailedRepair_FailedRecoveryRename_KeepsBothSurvivors")]
    public async Task ArchiveReplacedBeforeAFailedRepair(bool failRecovery)
    {
        await DamagedAsync(Short);
        var archive = Archive("pack.zip", Rom);
        var hooks = new ExternalIngestHooks
        {
            Writer = FailVerify(() =>
            {
                File.Delete(archive);
                Archive("pack.zip", RandomNumberGenerator.GetBytes(16));
            }),
            Recovery = failRecovery
                ? new ExternalMoveHooks { Rename = (_, _) => new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused") }
                : null,
        };

        await Job(Pipeline(hooks)).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        var item = _items.Single(i => i.ArchiveEntry == "dump.nsp");
        Assert.That((item.Outcome, item.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.VerifyFailed)));
        var orphan = Path.Combine(Bucket, "Space Game.(USA) (2).xci");
        if (failRecovery)
        {
            Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom), "the verified incoming copy stays where it is");
            Assert.That(File.ReadAllBytes(Backup), Is.EqualTo(Short), "and so does the original");
            Assert.That(item.Details, Does.Contain(Sidecar).And.Contain(Backup));
        }
        else
        {
            Assert.That(File.ReadAllBytes(orphan), Is.EqualTo(Rom));
            Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Short));
            Assert.That(File.Exists(Backup), Is.False);
            Assert.That(item.Details, Does.Contain(orphan));
        }
    }

    // --- B14: set files repair themselves ---

    private const string SetZipRelative = "extset/S/Set Game.zip";

    private async Task<long> InsertSetIssueAsync(string zipPath, string entryName, Guid gameId)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.ExecuteScalarAsync<long>("""
            INSERT INTO integrity_issues (type, severity, system_id, zip_path, game_id, entry_name, status, first_seen_job_id, last_seen_job_id, detected_at)
            VALUES ('EntryMissing', 'Error', 'extset', @zipPath, @gameId, @entryName, 'Open', @job, @job, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """, new { zipPath, entryName, gameId = gameId.ToString(), job = Guid.NewGuid().ToString() });
    }

    /// <summary>The set stored, then its Track 2 deleted from the library with an open <c>EntryMissing</c>.</summary>
    private async Task<long> SetWithMissingTrack2Async()
    {
        DropSetGame();
        await RunJobAsync(Pipeline());
        var gameId = _items.First(i => i.GameId is not null).GameId!.Value;
        File.Delete(Path.Combine(SetFolder('S'), SetTrack2));
        _items.Clear();
        return await InsertSetIssueAsync(SetZipRelative, SetTrack2, gameId);
    }

    [Test]
    public async Task SetFile_MissingAudioTrack_IsPutBackByReingestingTheSet()
    {
        var issue = await SetWithMissingTrack2Async();
        DropSetGame();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(new[]
        {
            (SetCue, IngestOutcome.Duplicate, (string?)null),
            (SetTrack1, IngestOutcome.Duplicate, null),
            (SetTrack2, IngestOutcome.Repaired, null),
        }));
        Assert.That(File.ReadAllBytes(Path.Combine(SetFolder('S'), SetTrack2)), Is.EqualTo(Audio));
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo("Resolved"));
        Assert.That((await AuditAsync()).Count(a => a.Action == AuditActions.ZipRepaired), Is.EqualTo(1));
    }

    [Test]
    public async Task SetFile_LoneTrack_RepairsToo()
    {
        var issue = await SetWithMissingTrack2Async();

        var result = await IngestAsync(Pipeline(), DropSet(SetTrack2, Audio));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Repaired, (string?)null)));
        Assert.That(File.ReadAllBytes(Path.Combine(SetFolder('S'), SetTrack2)), Is.EqualTo(Audio));
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task SetFile_MatchingNameWithAnotherSha1_IsNotUsedForARepair()
    {
        var issue = await SetWithMissingTrack2Async();
        DropDisc(SetCue, (SetTrack1, SetData), (SetTrack2, Audio2));

        await RunJobAsync(Pipeline());

        Assert.That(SetItems().Single(i => i.Name == SetTrack2), Is.EqualTo((SetTrack2, IngestOutcome.Duplicate, (string?)null)));
        Assert.That(File.Exists(Path.Combine(SetFolder('S'), SetTrack2)), Is.False);
        Assert.That(await IssueStatusAsync(issue), Is.EqualTo("Open"));
    }

    [Test]
    public async Task SetFile_LoneTrackMatchingOpenIssuesOfTwoGames_GoesToReview()
    {
        var alpha = RandomNumberGenerator.GetBytes(2352 * 2);
        var bravo = RandomNumberGenerator.GetBytes(2352 * 2 + 4);
        WriteDats(new FixtureGame("Alpha Game (USA)", "Alpha (Track 1).bin", alpha), new FixtureGame("Bravo Game (USA)", "Bravo (Track 1).bin", bravo));
        const string common = "Common (Track 2).bin";
        var owners = new List<(string Zip, Guid Game)>();
        foreach (var (cue, track, bytes, zip) in new[] { ("Alpha.cue", "Alpha (Track 1).bin", alpha, "extset/A/Alpha Game.zip"), ("Bravo.cue", "Bravo (Track 1).bin", bravo, "extset/B/Bravo Game.zip") })
        {
            DropDisc(cue, (track, bytes), (common, SharedAudio));
            _items.Clear();
            await RunJobAsync(Pipeline());
            Assert.That(SetItems().Select(i => i.Outcome), Is.All.EqualTo(IngestOutcome.Ingested));
            owners.Add((zip, _items.First(i => i.GameId is not null).GameId!.Value));
        }
        foreach (var (zip, game) in owners)
        {
            File.Delete(Path.Combine(_factory.Root, "roms", zip[..zip.LastIndexOf('/')], common));
            await InsertSetIssueAsync(zip, common, game);
        }

        var result = await IngestAsync(Pipeline(), DropSet(common, SharedAudio));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
    }
}
