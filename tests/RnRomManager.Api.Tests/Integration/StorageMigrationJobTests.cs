using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>S4–S6: the storage migration job moves legacy games' embedded ROMs next to their ZIPs.</summary>
[Category("Integration")]
public class StorageMigrationJobTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private ApiFactory _factory = null!;
    private int _seed;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => SystemHandlerRegistration.AddSystemHandler<LegacyExternalTestHandler>(services),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public Task SetUp() => _factory.ResetAsync();

    private string Roms => Path.Combine(_factory.Root, "roms");

    private sealed record Legacy(InfoJsonGame Game, string ZipPath, string Zip, byte[][] Roms, byte[] ZipBytes)
    {
        public string Folder => Path.GetDirectoryName(Zip)!;
    }

    /// <summary>A mirrored legacy game with <paramref name="romCount"/> releases, a note on the first and a cover image.</summary>
    private async Task<Legacy> WriteLegacy(string title, int romCount = 1, Func<InfoJsonGame, InfoJsonGame>? change = null)
    {
        var bytes = Enumerable.Range(0, romCount).Select(_ => TestLibrary.RomBytes(++_seed)).ToArray();
        var image = new InfoJsonImage
        {
            FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceUpload, IsDefault = false, IsCover = true,
            SizeBytes = Png.Length, Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Png)), Width = 1, Height = 1,
            DateAdded = TestLibrary.When,
        };
        var game = TestLibrary.Game(title, [.. bytes.Select((b, i) => TestLibrary.Rom($"{title} ({i}).nds", b, isPrimary: i == 0))]) with
        {
            System = LegacyExternalTestHandler.SystemId, Images = [image],
        };
        game = change?.Invoke(game) ?? game;
        var zipPath = $"legacy/{title[..1]}/{title}.zip";
        var zip = Path.GetFullPath(Path.Combine(Roms, zipPath));
        LegacyZipWriter.Write(zip, game, new Dictionary<string, byte[]>
        {
            [LibraryZip.ImageEntry(image.FileName)] = Png,
            [LibraryZip.NoteEntry(game.Roms[0].FileName)] = "a note"u8.ToArray(),
        }, bytes);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, new FileInfo(zip).Length, DateTime.UtcNow), default);
        return new(game, zipPath, zip, bytes, File.ReadAllBytes(zip));
    }

    private StorageMigrationJob Job(LibraryZipWriterHooks? hooks = null, IFreeSpace? free = null)
    {
        var job = free is null ? Service<StorageMigrationJob>() : ActivatorUtilities.CreateInstance<StorageMigrationJob>(_factory.Services, free);
        job.WriterHooks = hooks;
        return job;
    }

    /// <summary>Runs the job directly (no lock), under a <c>jobs</c> row its items can reference.</summary>
    private async Task<JsonElement> RunAsync(StorageMigrationJob job, Guid jobId, CancellationToken ct = default)
    {
        await Service<IJobRepository>().InsertAsync(new Job
        {
            Id = jobId, Type = JobType.StorageMigration, Trigger = JobTrigger.Manual, Status = JobStatus.Running, StartedAt = DateTime.UtcNow,
        }, default);
        return JsonSerializer.SerializeToElement(await job.RunAsync(new JobContext(jobId, new JobProgress()), ct));
    }

    private async Task<IReadOnlyList<JobItem>> Items(Guid jobId) =>
        (await Service<IJobItemRepository>().ListAsync(jobId, null, 1, 100, default)).Items;

    private InfoJsonGame ReadZip(string zip)
    {
        using var library = LibraryZip.Open(zip, Service<InfoJsonReader>());
        return library.InfoJson!.Game!;
    }

    private static string[] EntryNames(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    [Test]
    public async Task LegacyGame_IsMigrated_FilesNextToTheZip_EntriesKept_Mirrored_Audited_AndItsItemAndSummaryPersisted()
    {
        var legacy = await WriteLegacy("Alpha", romCount: 2);

        var started = await Service<JobRunner>().StartAsync(Job(), JobTrigger.Startup, null, default);
        await started!.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var game = ReadZip(legacy.Zip);
        Assert.That(game.Roms.Select(r => r.ExternalFileName), Is.All.Not.Null);
        for (var i = 0; i < legacy.Roms.Length; i++)
            Assert.That(File.ReadAllBytes(Path.Combine(legacy.Folder, game.Roms[i].ExternalFileName!)), Is.EqualTo(legacy.Roms[i]));
        Assert.That(EntryNames(legacy.Zip), Is.EqualTo(new[] { "images/boxart-01.png", LibraryZip.InfoJsonEntry, LibraryZip.NoteEntry("Alpha (0).nds") }));
        Assert.That(InfoJsonSerializer.Serialize(game with { Roms = legacy.Game.Roms }), Is.EqualTo(InfoJsonSerializer.Serialize(legacy.Game)),
            "only the pointers changed");

        var mirrored = (await Service<IRomRepository>().ListExternalAsync(default)).Where(r => r.GameId == legacy.Game.GameId).ToList();
        Assert.That(mirrored.Select(r => r.ExternalFileName), Is.EquivalentTo(game.Roms.Select(r => r.ExternalFileName)));
        Assert.That(await Service<IGameRepository>().ListLegacyEmbeddedAsync([LegacyExternalTestHandler.SystemId], default), Is.Empty);

        var row = (await Service<IJobRepository>().GetAsync(started.Id, default))!;
        Assert.That((row.Type, row.Trigger, row.Status, row.Mode, row.RequestedBy),
            Is.EqualTo((JobType.StorageMigration, JobTrigger.Startup, JobStatus.Completed, (JobMode?)null, (int?)null)));
        var summary = JsonDocument.Parse(row.Summary!).RootElement;
        Assert.That((summary.GetProperty("migrated").GetInt32(), summary.GetProperty("error").GetInt32()), Is.EqualTo((1, 0)));
        var item = (await Items(started.Id)).Single();
        Assert.That((item.FileId, item.GameId, item.SystemId, item.SourcePath, item.Outcome, item.Reason),
            Is.EqualTo((legacy.Game.GameId, (Guid?)legacy.Game.GameId, (string?)"legacy", "roms/legacy/A/Alpha.zip", "Migrated", (string?)null)));

        await Service<RnRomManager.Api.AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        var audit = (await db.QueryAsync<(string Action, string Path, string Details)>(
            "SELECT action, path, details FROM audit_events WHERE action = 'RomsExternalized'")).Single();
        var bytes = legacy.Roms.Sum(r => (long)r.Length);
        Assert.That(audit, Is.EqualTo((AuditActions.RomsExternalized, "roms/legacy/A/Alpha.zip", $"2 ROM(s), {bytes} bytes")));
    }

    [Test]
    public async Task Sha1Mismatch_IsVerifyFailed_LeavesTheZip_AndDeletesTheFilesItCreated()
    {
        // The second ROM's info.json SHA-1 doesn't match its entry, so the first one was already moved out.
        var legacy = await WriteLegacy("Alpha", romCount: 2, game => game with
        {
            Roms = [game.Roms[0], game.Roms[1] with { Metadata = game.Roms[1].Metadata with { Sha1 = new string('a', 40) } }],
        });
        var jobId = Guid.NewGuid();

        var summary = await RunAsync(Job(), jobId);

        Assert.That(summary.GetProperty("error").GetInt32(), Is.EqualTo(1));
        var item = (await Items(jobId)).Single();
        Assert.That((item.Outcome, item.Reason), Is.EqualTo(("Error", (string?)"VerifyFailed")));
        Assert.That(File.ReadAllBytes(legacy.Zip), Is.EqualTo(legacy.ZipBytes));
        Assert.That(Directory.GetFiles(legacy.Folder), Is.EqualTo(new[] { legacy.Zip }));
    }

    [Test]
    public async Task IoFailureBeforeThePublish_DeletesTheMovedFiles_AndLeavesTheZip()
    {
        var legacy = await WriteLegacy("Alpha", romCount: 2);
        var jobId = Guid.NewGuid();

        await RunAsync(Job(new LibraryZipWriterHooks { AfterTempClosed = () => throw new IOException("disk full") }), jobId);

        var item = (await Items(jobId)).Single();
        Assert.That((item.Outcome, item.Reason), Is.EqualTo(("Error", (string?)"IoError")));
        Assert.That(File.ReadAllBytes(legacy.Zip), Is.EqualTo(legacy.ZipBytes));
        Assert.That(Directory.GetFiles(legacy.Folder), Is.EqualTo(new[] { legacy.Zip }));
    }

    [Test]
    public async Task ASameNamedFile_GetsTheNextCandidate()
    {
        var legacy = await WriteLegacy("Alpha");
        var taken = Path.Combine(legacy.Folder, ExternalRomName.Build("Alpha", "Alpha (0).nds"));
        File.WriteAllBytes(taken, [1, 2, 3]);

        await RunAsync(Job(), Guid.NewGuid());

        Assert.That(ReadZip(legacy.Zip).Roms[0].ExternalFileName, Is.EqualTo(ExternalRomName.Build("Alpha", "Alpha (0).nds", 2)));
        Assert.That(File.ReadAllBytes(taken), Is.EqualTo(new byte[] { 1, 2, 3 }), "the occupant is left alone");
    }

    [Test]
    public async Task ACrashLeftover_WithTheRomsSizeAndSha1_IsReused()
    {
        var legacy = await WriteLegacy("Alpha");
        var name = ExternalRomName.Build("Alpha", "Alpha (0).nds");
        var leftover = Path.Combine(legacy.Folder, name);
        File.WriteAllBytes(leftover, legacy.Roms[0]);
        var before = File.GetLastWriteTimeUtc(leftover).AddMinutes(-5);
        File.SetLastWriteTimeUtc(leftover, before);

        await RunAsync(Job(), Guid.NewGuid());

        Assert.That(ReadZip(legacy.Zip).Roms[0].ExternalFileName, Is.EqualTo(name));
        Assert.That(File.GetLastWriteTimeUtc(leftover), Is.EqualTo(before), "the leftover itself is used, not replaced");
        Assert.That(Directory.GetFiles(legacy.Folder).Order(), Is.EqualTo(new[] { leftover, legacy.Zip }.Order()));
        var work = Path.Combine(_factory.Root, "cache", "work", StorageMigrationJob.WorkFolder);
        Assert.That(!Directory.Exists(work) || !Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Any(), Is.True,
            "the temp copy is gone");
    }

    [Test]
    public async Task ASameNamedFileWithOtherBytesOfTheSameSize_GetsTheNextCandidate()
    {
        var legacy = await WriteLegacy("Alpha");
        var taken = Path.Combine(legacy.Folder, ExternalRomName.Build("Alpha", "Alpha (0).nds"));
        var other = legacy.Roms[0].ToArray();
        other[^1] ^= 0xFF;
        File.WriteAllBytes(taken, other);

        await RunAsync(Job(), Guid.NewGuid());

        Assert.That(ReadZip(legacy.Zip).Roms[0].ExternalFileName, Is.EqualTo(ExternalRomName.Build("Alpha", "Alpha (0).nds", 2)));
        Assert.That(File.ReadAllBytes(taken), Is.EqualTo(other));
    }

    [Test]
    public async Task AFileClaimedByARomRow_IsNeverReused()
    {
        var legacy = await WriteLegacy("Alpha");
        var name = ExternalRomName.Build("Alpha", "Alpha (0).nds");
        var claimed = Path.Combine(legacy.Folder, name);
        File.WriteAllBytes(claimed, legacy.Roms[0]);
        var owner = TestLibrary.Game("Other", TestLibrary.Rom("Other (USA).nds", TestLibrary.RomBytes(++_seed)) with { ExternalFileName = name })
            with { System = LegacyExternalTestHandler.SystemId };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(owner, "legacy/A/Other.zip", 100, DateTime.UtcNow), default);

        await RunAsync(Job(), Guid.NewGuid());

        Assert.That(ReadZip(legacy.Zip).Roms[0].ExternalFileName, Is.EqualTo(ExternalRomName.Build("Alpha", "Alpha (0).nds", 2)));
        Assert.That(File.Exists(claimed), Is.True);
    }

    private sealed class FakeFreeSpace(Func<string, long?> free) : IFreeSpace
    {
        public long? AvailableBytes(string path) => free(path);
    }

    [TestCase(true, TestName = "ShortSpace_IsInsufficientSpace_AndTheNextGameContinues library volume")]
    [TestCase(false, TestName = "ShortSpace_IsInsufficientSpace_AndTheNextGameContinues cache volume")]
    public async Task ShortSpace_IsInsufficientSpace_AndTheNextGameContinues(bool library)
    {
        var alpha = await WriteLegacy("Alpha");
        var beta = await WriteLegacy("Beta");
        var cache = Path.GetFullPath(Path.Combine(_factory.Root, "cache"));
        var needed = alpha.Roms[0].Length + alpha.ZipBytes.Length + StorageMigrationJob.SpaceMarginBytes;
        string? lastZip = null;
        // The library check gets the ZIP's path, then the cache check the cache's: only Alpha is short, on one of them.
        var free = new FakeFreeSpace(path =>
        {
            if (!path.StartsWith(cache, StringComparison.OrdinalIgnoreCase))
            {
                lastZip = path;
                return library && path.Contains("Alpha", StringComparison.Ordinal) ? needed - 1 : needed;
            }
            return !library && lastZip!.Contains("Alpha", StringComparison.Ordinal) ? alpha.Roms[0].Length - 1 : long.MaxValue;
        });
        var jobId = Guid.NewGuid();

        var summary = await RunAsync(Job(free: free), jobId);

        Assert.That((summary.GetProperty("migrated").GetInt32(), summary.GetProperty("error").GetInt32()), Is.EqualTo((1, 1)));
        var items = await Items(jobId);
        Assert.That(items.Select(i => (i.GameId, i.Outcome, i.Reason)), Is.EqualTo(new[]
        {
            ((Guid?)alpha.Game.GameId, "Error", (string?)"InsufficientSpace"),
            ((Guid?)beta.Game.GameId, "Migrated", (string?)null),
        }));
        Assert.That(File.ReadAllBytes(alpha.Zip), Is.EqualTo(alpha.ZipBytes));
    }

    [Test]
    public async Task UnreadableFreeSpace_IsLogged_AndTheGameMigrates()
    {
        var legacy = await WriteLegacy("Alpha");

        var summary = await RunAsync(Job(free: new FakeFreeSpace(_ => null)), Guid.NewGuid());

        Assert.That(summary.GetProperty("migrated").GetInt32(), Is.EqualTo(1));
        Assert.That(ReadZip(legacy.Zip).Roms[0].ExternalFileName, Is.Not.Null);
    }

    [Test]
    public async Task Cancel_StopsBetweenGames()
    {
        var alpha = await WriteLegacy("Alpha");
        var beta = await WriteLegacy("Beta");
        using var cts = new CancellationTokenSource();
        var jobId = Guid.NewGuid();

        var summary = await RunAsync(Job(new LibraryZipWriterHooks { AfterTempClosed = cts.Cancel }), jobId, cts.Token);

        Assert.That((summary.GetProperty("migrated").GetInt32(), summary.GetProperty("error").GetInt32()), Is.EqualTo((1, 0)));
        Assert.That(ReadZip(alpha.Zip).Roms[0].ExternalFileName, Is.Not.Null, "the game in progress finishes");
        Assert.That(File.ReadAllBytes(beta.Zip), Is.EqualTo(beta.ZipBytes));
        Assert.That((await Items(jobId)).Select(i => i.GameId), Is.EqualTo(new Guid?[] { alpha.Game.GameId }));
    }

    [Test]
    public async Task ACandidateWhoseZipIsNoLongerLegacy_IsSkipped_WithNoItem()
    {
        var legacy = await WriteLegacy("Alpha");
        await RunAsync(Job(), Guid.NewGuid());
        var migrated = File.ReadAllBytes(legacy.Zip);
        // A stale mirror still lists the embedded ROM.
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(legacy.Game, legacy.ZipPath, migrated.Length, DateTime.UtcNow), default);
        var jobId = Guid.NewGuid();

        var summary = await RunAsync(Job(), jobId);

        Assert.That((summary.GetProperty("migrated").GetInt32(), summary.GetProperty("error").GetInt32()), Is.EqualTo((0, 0)));
        Assert.That(await Items(jobId), Is.Empty);
        Assert.That(File.ReadAllBytes(legacy.Zip), Is.EqualTo(migrated));
    }
}
