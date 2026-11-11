using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.Core.Integrity.IntegrityIssueType;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Spec §12 integrity checker (S36): every §15.1 "Integrity" row, against MariaDB + temp ZIPs.</summary>
[Category("Integration")]
public class IntegrityJobTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 2, 0, 0, 0, 2, 0,
        8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];

    private MySqlConnectionFactory _factory = null!;
    private GameRepository _games = null!;
    private IntegrityIssueRepository _issues = null!;
    private JobRepository _jobs = null!;
    private InfoJsonReader _reader = null!;
    private ISystemRegistry _systems = null!;
    private DbSync _sync = null!;
    private TempDir _dir = null!;
    private Action? _onScan;
    private int _seed;
    private DateTime _mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public async Task SetUp()
    {
        _factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(_factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _games = new GameRepository(_factory);
        _issues = new IntegrityIssueRepository(_factory);
        _jobs = new JobRepository(_factory);
        var handlers = new[] { "gb", "gbc" }.Select(id =>
        {
            var handler = Substitute.For<ISystemHandler>();
            handler.EmbedRomsInZip.Returns(true);
            handler.Id.Returns(id);
            return handler;
        }).Append(new ExternalTestHandler()).ToArray();
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns(handlers);
        _systems.ById(Arg.Any<string>()).Returns(call =>
        {
            _onScan?.Invoke();
            return handlers.FirstOrDefault(h => h.Id == call.Arg<string>());
        });
        _reader = new InfoJsonReader([], _systems);
        _dir = new TempDir();
        _sync = new DbSync(_games, new RomRepository(_factory), new LibraryMirror(_factory), _reader, _systems,
            Options.Create(new PathsOptions { Roms = _dir.Path }), NullLogger<DbSync>.Instance);
        _onScan = null;
        _seed = 0;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Abs(string relative) => _dir.Combine(relative.Split('/'));

    private static InfoJsonImage BoxArt(bool isDefault) => new()
    {
        FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = isDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceLibretro,
        IsDefault = isDefault, IsCover = true, SizeBytes = Png.Length, Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Png)),
        Width = 2, Height = 2, DateAdded = When,
    };

    /// <summary>Writes a valid one-ROM game ZIP with a real (or default, or no) box art; returns its info.json.</summary>
    private async Task<InfoJsonGame> Write(string zipPath, string title, bool? defaultBoxArt = false, Guid? gameId = null, int? seed = null)
    {
        var romSeed = seed ?? ++_seed;
        var rom = Rom($"{title} (World).gb", RomBytes(romSeed));
        var game = Game(title, rom) with { Images = defaultBoxArt is { } d ? [BoxArt(d)] : [] };
        if (gameId is { } id)
            game = game with { GameId = id };
        var path = Abs(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(RomBytes(romSeed)) };
        if (defaultBoxArt is not null)
            entries[LibraryZip.ImageEntry("boxart-01.png")] = () => new MemoryStream(Png);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, entries));
        Touch(zipPath);
        return game;
    }

    /// <summary>Edits a ZIP in place (the test plays a damaged file), keeping its info.json.</summary>
    private void Damage(string zipPath, Action<ZipArchive> change)
    {
        using (var zip = ZipFile.Open(Abs(zipPath), ZipArchiveMode.Update))
            change(zip);
        Touch(zipPath);
    }

    /// <summary>A distinct whole-second mtime per write, so a same-size rewrite still counts as changed (§12.1).</summary>
    private void Touch(string zipPath)
    {
        _mtime = _mtime.AddSeconds(10);
        File.SetLastWriteTimeUtc(Abs(zipPath), _mtime);
    }

    private static void Replace(ZipArchive zip, string entry, byte[] bytes)
    {
        zip.GetEntry(entry)?.Delete();
        using var stream = zip.CreateEntry(entry).Open();
        stream.Write(bytes);
    }

    private async Task<(Guid JobId, string Summary)> Run(JobMode mode = JobMode.Quick, CancellationToken ct = default, IntegrityHooks? hooks = null)
    {
        var jobId = Guid.NewGuid();
        var job = new IntegrityJob(mode, _games, new RomRepository(_factory), _sync, _issues, _jobs, _reader, _systems, Options.Create(new PathsOptions { Roms = _dir.Path }),
            TimeProvider.System, NullLogger<IntegrityJob>.Instance) { Hooks = hooks };
        var summary = await job.RunAsync(new JobContext(jobId, new JobProgress()), ct);
        return (jobId, System.Text.Json.JsonSerializer.Serialize(summary));
    }

    private async Task<IReadOnlyList<(string Type, string? ZipPath, string? Entry)>> Open() =>
        (await _issues.ListAllOpenAsync(default)).Select(i => (i.Type, i.ZipPath, i.EntryName)).ToList();

    private async Task<List<IntegrityIssue>> All()
    {
        await using var connection = _factory.Create();
        return (await Dapper.SqlMapper.QueryAsync<IntegrityIssue>(connection, """
            SELECT id AS Id, type AS Type, severity AS Severity, system_id AS SystemId, zip_path AS ZipPath, game_id AS GameId,
                   entry_name AS EntryName, details AS Details, status AS Status, first_seen_job_id AS FirstSeenJobId,
                   last_seen_job_id AS LastSeenJobId, detected_at AS DetectedAt, resolved_at AS ResolvedAt
            FROM integrity_issues ORDER BY id
            """)).ToList();
    }

    [Test]
    public async Task HealthyLibrary_FirstRun_RepairsDbAsAutoRepaired_DefaultBoxArtIsInfo()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris", defaultBoxArt: true);

        await Run();

        var all = await All();
        Assert.That(all.Select(i => (i.Type, i.Severity, i.Status)), Is.EquivalentTo(new[]
        {
            (DbRowInserted, IssueSeverity.Info, IntegrityIssue.AutoRepaired),
            (DefaultBoxArt, IssueSeverity.Info, IntegrityIssue.Open),
        }));
        Assert.That(all.All(i => i.ZipPath == "gb/T/Tetris.zip" && i.GameId == game.GameId && i.SystemId == "gb"), Is.True);
        Assert.That(await _games.GetAsync(game.GameId, default), Is.Not.Null);
    }

    [Test]
    public async Task UnreadableOrBadInfoJson_EachGetsItsType()
    {
        Directory.CreateDirectory(_dir.Combine("gb", "A"));
        await File.WriteAllBytesAsync(Abs("gb/A/Alpha.zip"), [1, 2, 3]);
        await Write("gb/B/Beta.zip", "Beta");
        Damage("gb/B/Beta.zip", zip => zip.GetEntry("info.json")!.Delete());
        await Write("gb/C/Gamma.zip", "Gamma");
        Damage("gb/C/Gamma.zip", zip => Replace(zip, "info.json", "{ not json"u8.ToArray()));
        await Write("gb/D/Delta.zip", "Delta");
        Damage("gb/D/Delta.zip", zip =>
        {
            JsonNode info;
            using (var reader = new StreamReader(zip.GetEntry("info.json")!.Open()))
                info = JsonNode.Parse(reader.ReadToEnd())!;
            info["schemaVersion"] = InfoJsonReader.CurrentVersion + 1;
            Replace(zip, "info.json", System.Text.Encoding.UTF8.GetBytes(info.ToJsonString()));
        });

        await Run();

        var open = (await _issues.ListAllOpenAsync(default)).ToDictionary(i => i.ZipPath!, i => (i.Type, i.Severity));
        Assert.That(open["gb/A/Alpha.zip"], Is.EqualTo((ZipUnreadable, IssueSeverity.Error)));
        Assert.That(open["gb/B/Beta.zip"], Is.EqualTo((InfoJsonMissing, IssueSeverity.Error)));
        Assert.That(open["gb/C/Gamma.zip"], Is.EqualTo((InfoJsonInvalid, IssueSeverity.Error)));
        Assert.That(open["gb/D/Delta.zip"], Is.EqualTo((InfoJsonNewerSchema, IssueSeverity.Warn)));
        Assert.That(await _games.ListAllAsync(default), Is.Empty, "unreadable ZIPs get no rows");
    }

    [Test]
    public async Task EntryProblems_AndMissingBoxArt()
    {
        await Write("gb/T/Tetris.zip", "Tetris");
        Damage("gb/T/Tetris.zip", zip =>
        {
            zip.GetEntry("images/boxart-01.png")!.Delete();
            Replace(zip, "roms/Tetris (World).gb", new byte[100]);
            Replace(zip, "roms/extra.gb", [1]);
        });
        await Write("gb/N/No Art.zip", "No Art", defaultBoxArt: null);

        await Run();

        Assert.That(await Open(), Is.EquivalentTo(new (string, string?, string?)[]
        {
            (EntryMissing, "gb/T/Tetris.zip", "images/boxart-01.png"),
            (EntrySizeMismatch, "gb/T/Tetris.zip", "roms/Tetris (World).gb"),
            (EntryUnlisted, "gb/T/Tetris.zip", "roms/extra.gb"),
            (MissingBoxArt, "gb/N/No Art.zip", null),
        }));
    }

    [Test]
    public async Task Notes_OfListedRoms_AreNotReported_OrphanNotesAre()
    {
        await Write("gb/T/Tetris.zip", "Tetris");
        Damage("gb/T/Tetris.zip", zip =>
        {
            Replace(zip, "notes/Tetris (World).gb.md", [1]);
            Replace(zip, "notes/Gone.gb.md", [1]);
        });

        await Run();

        Assert.That(await Open(), Is.EquivalentTo(new (string, string?, string?)[]
        {
            (EntryUnlisted, "gb/T/Tetris.zip", "notes/Gone.gb.md"),
        }));
    }

    [Test]
    public async Task BoxArtCodes_AreAboutTheCover()
    {
        var snap = BoxArt(false) with { FileName = "snap-01.png", Type = ImageType.Snap };
        await WriteWith("gb/S/Snap Cover.zip", "Snap Cover", [snap]);
        await WriteWith("gb/P/Placeholder Kept.zip", "Placeholder Kept", [BoxArt(true) with { IsCover = false }, snap]);
        await WriteWith("gb/D/Default Cover.zip", "Default Cover", [BoxArt(true), snap with { IsCover = false }]);

        await Run();

        Assert.That(await Open(), Is.EquivalentTo(new (string, string?, string?)[] { (DefaultBoxArt, "gb/D/Default Cover.zip", null) }),
            "a snap cover isn't MissingBoxArt; a placeholder that isn't the cover isn't DefaultBoxArt");
    }

    /// <summary>A one-ROM game ZIP with these images (each stored with the test PNG).</summary>
    private async Task WriteWith(string zipPath, string title, InfoJsonImage[] images)
    {
        var romSeed = ++_seed;
        var rom = Rom($"{title} (World).gb", RomBytes(romSeed));
        var game = Game(title, rom) with { Images = images };
        var path = Abs(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(RomBytes(romSeed)) };
        foreach (var image in images)
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(Png);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, entries));
        Touch(zipPath);
    }

    [Test]
    public async Task HashMismatch_OnlyDeep_QuickDoesntOpenUnchangedZips()
    {
        await Write("gb/T/Tetris.zip", "Tetris");
        await Run(); // syncs the DB
        var rom = RomBytes(1);
        rom[0x200] ^= 0xFF;
        Damage("gb/T/Tetris.zip", zip => Replace(zip, "roms/Tetris (World).gb", rom));

        await Run(JobMode.Quick);
        Assert.That(await Open(), Is.Empty, "quick compares sizes, never hashes");

        await Run(JobMode.Deep);
        Assert.That(await Open(), Is.EqualTo(new (string, string?, string?)[] { (HashMismatch, "gb/T/Tetris.zip", "roms/Tetris (World).gb") }));

        // Same size + mtime as the DB row: quick checks only the location, deep opens it.
        var path = Abs("gb/T/Tetris.zip");
        var (length, mtime) = (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
        await Run(JobMode.Quick); // the DB row now matches the damaged file
        await File.WriteAllBytesAsync(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, mtime);
        await Run(JobMode.Quick);
        Assert.That((await Open()).Select(i => i.Type), Does.Not.Contain(ZipUnreadable));
        await Run(JobMode.Deep);
        Assert.That((await Open()).Select(i => i.Type), Does.Contain(ZipUnreadable));
    }

    [Test]
    public async Task WrongLocation_SystemBucketOrName_ClashSuffixAndCaseAccepted()
    {
        await Write("gb/Tetris.zip", "Tetris");
        await Write("gb/X/Xenon.zip", "Alpha");
        await Write("gbc/C/Color.zip", "Color");
        await Write("gb/W/Wrong Name.zip", "Right Name");
        await Write("gb/Z/Zelda (2).zip", "Zelda");
        await Write("gb/q/quest.zip", "Quest");

        await Run();

        Assert.That((await Open()).Where(i => i.Type == WrongLocation).Select(i => i.ZipPath),
            Is.EquivalentTo(new[] { "gb/Tetris.zip", "gb/X/Xenon.zip", "gbc/C/Color.zip", "gb/W/Wrong Name.zip" }));
        Assert.That((await _games.ListAllAsync(default)).Select(g => g.ZipPath), Has.Member("gb/Tetris.zip"), "DB repaired as normal");
    }

    [Test]
    public async Task Scope_RootFiles_UnregisteredFolders_StaleTmp_AreOrphans_NeverOpened()
    {
        Directory.CreateDirectory(_dir.Combine("md", "S"));
        Directory.CreateDirectory(_dir.Combine("gb", "T"));
        await File.WriteAllTextAsync(_dir.Combine("readme.txt"), "x");
        await File.WriteAllBytesAsync(_dir.Combine("md", "S", "Game.zip"), [1, 2, 3]);
        await File.WriteAllBytesAsync(_dir.Combine("gb", "T", "Tetris.zip.tmp"), [1]);
        await File.WriteAllTextAsync(_dir.Combine("gb", "T", "notes.txt"), "x");

        await Run();

        Assert.That(await Open(), Is.EquivalentTo(new (string, string?, string?)[]
        {
            (OrphanFile, "readme.txt", null), (OrphanFile, "md/S/Game.zip", null),
            (OrphanFile, "gb/T/Tetris.zip.tmp", null), (OrphanFile, "gb/T/notes.txt", null),
        }));
        Assert.That((await All()).All(i => i.Severity == IssueSeverity.Warn), Is.True);
    }

    [Test]
    public async Task DbRepair_InsertUpdateDelete_RecordedAsAutoRepaired()
    {
        var keep = await Write("gb/T/Tetris.zip", "Tetris");
        var gone = await Write("gb/Z/Zelda.zip", "Zelda");
        await Run();
        File.Delete(Abs("gb/Z/Zelda.zip"));
        await Write("gb/T/Tetris.zip", "Tetris", gameId: keep.GameId, seed: 99);
        await Write("gb/A/Alpha.zip", "Alpha");

        var (jobId, _) = await Run();

        var repairs = (await All()).Where(i => i.LastSeenJobId == jobId && i.Status == IntegrityIssue.AutoRepaired)
            .Select(i => (i.Type, i.ZipPath)).ToList();
        Assert.That(repairs, Is.EquivalentTo(new[] { (DbRowUpdated, "gb/T/Tetris.zip"), (DbRowDeleted, "gb/Z/Zelda.zip"), (DbRowInserted, "gb/A/Alpha.zip") }));
        Assert.That(await _games.GetAsync(gone.GameId, default), Is.Null);
    }

    [Test]
    public async Task DuplicateGameIdAndSha1_AreReported_AndTheirDbRepairIsSkipped()
    {
        var twin = await Write("gb/T/Tetris.zip", "Tetris");
        await Write("gb/T/Tetris (2).zip", "Tetris", gameId: twin.GameId, seed: 50);
        await Write("gb/A/Alpha.zip", "Alpha", seed: 7);
        await Write("gb/B/Beta.zip", "Beta", seed: 7);

        await Run();

        var open = await _issues.ListAllOpenAsync(default);
        Assert.That(open.Where(i => i.Type == DuplicateGameId).Select(i => i.ZipPath), Is.EquivalentTo(new[] { "gb/T/Tetris.zip", "gb/T/Tetris (2).zip" }));
        Assert.That(open.Single(i => i.Type == DuplicateGameId && i.ZipPath == "gb/T/Tetris.zip").Details, Does.Contain("updatedAt"));
        var sha1 = open.Single(i => i.Type == DuplicateSha1);
        Assert.That((sha1.ZipPath, sha1.Severity), Is.EqualTo(("gb/B/Beta.zip", IssueSeverity.Warn)));
        var rows = (await _games.ListAllAsync(default)).Select(g => g.ZipPath).ToList();
        Assert.That(rows, Is.EquivalentTo(new[] { "gb/A/Alpha.zip" }), "no rows for the duplicates");
    }

    [Test]
    public async Task SecondRun_UpdatesTheSameIssue_ThenAFixResolvesIt()
    {
        await File.WriteAllTextAsync(_dir.Combine("readme.txt"), "x");
        var (first, _) = await Run();
        var (second, _) = await Run();

        var issue = (await _issues.ListAllOpenAsync(default)).Single();
        Assert.That((issue.FirstSeenJobId, issue.LastSeenJobId), Is.EqualTo((first, second)));

        File.Delete(_dir.Combine("readme.txt"));
        await Run();
        var all = await All();
        Assert.That(all.Single().Status, Is.EqualTo(IntegrityIssue.Resolved));
        Assert.That(all.Single().ResolvedAt, Is.Not.Null);
    }

    [Test]
    public async Task D48_QuickRunDoesntResolveWhatItDidntCheck_DeepResolvesAfterTheFix()
    {
        await Write("gb/T/Tetris.zip", "Tetris");
        Damage("gb/T/Tetris.zip", zip => Replace(zip, "roms/extra.gb", [1]));
        await Run(JobMode.Quick);
        Assert.That((await Open()).Single().Type, Is.EqualTo(EntryUnlisted));

        await Run(JobMode.Quick); // unchanged now: location only
        Assert.That((await Open()).Single().Type, Is.EqualTo(EntryUnlisted), "still open");

        await Write("gb/T/Tetris.zip", "Tetris", seed: 1); // fixed (rewritten)
        await Run(JobMode.Quick);
        Assert.That(await Open(), Is.Empty);
    }

    [Test]
    public async Task D48_HashMismatch_StaysOpenAfterQuick_ResolvedOnlyByDeep()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris");
        var rom = RomBytes(1);
        rom[0x200] ^= 0xFF;
        Damage("gb/T/Tetris.zip", zip => Replace(zip, "roms/Tetris (World).gb", rom));
        await Run(JobMode.Deep);
        Assert.That((await Open()).Single().Type, Is.EqualTo(HashMismatch));

        await Write("gb/T/Tetris.zip", "Tetris", gameId: game.GameId, seed: 1); // fixed: the file changed, so quick opens it
        await Run(JobMode.Quick);
        Assert.That((await Open()).Single().Type, Is.EqualTo(HashMismatch), "quick never hashes");

        await Run(JobMode.Deep);
        Assert.That(await Open(), Is.Empty);
    }

    [Test]
    public async Task ZipGoneFromDisk_ResolvesItsIssues()
    {
        await Write("gb/T/Tetris.zip", "Tetris", defaultBoxArt: true);
        await Run();
        File.Delete(Abs("gb/T/Tetris.zip"));

        await Run();

        Assert.That(await Open(), Is.Empty);
    }

    [Test]
    public async Task CancelledRun_RecordsFindings_ButResolvesNothing()
    {
        await File.WriteAllTextAsync(_dir.Combine("old.txt"), "x");
        await Run();
        File.Delete(_dir.Combine("old.txt"));
        await File.WriteAllTextAsync(_dir.Combine("new.txt"), "x");
        await Write("gb/T/Tetris.zip", "Tetris");
        using var cancel = new CancellationTokenSource();
        _onScan = cancel.Cancel; // cancelled once the DB repair is done and the tree scan starts

        var (_, summary) = await Run(ct: cancel.Token);

        Assert.That(summary, Does.Contain("\"resolved\":0"));
        Assert.That((await Open()).Select(i => i.ZipPath), Is.EquivalentTo(new[] { "old.txt", "new.txt" }));
    }

    [Test]
    public async Task Retention_PurgesOldJobsAndClosedIssues_KeepsOpenAndRecent()
    {
        var now = DateTime.UtcNow;
        var oldJob = new Job { Id = Guid.NewGuid(), Type = JobType.Ingest, Trigger = JobTrigger.Manual, Status = JobStatus.Completed, StartedAt = now.AddDays(-181) };
        var recentJob = oldJob with { Id = Guid.NewGuid(), StartedAt = now.AddDays(-179) };
        await _jobs.InsertAsync(oldJob, default);
        await _jobs.InsertAsync(recentJob, default);
        IntegrityIssue Issue(string status, DateTime detected, DateTime? resolved, string zip) => new()
        {
            Type = OrphanFile, Severity = IssueSeverity.Warn, ZipPath = zip, Status = status, FirstSeenJobId = oldJob.Id,
            LastSeenJobId = oldJob.Id, DetectedAt = detected, ResolvedAt = resolved,
        };
        await _issues.InsertAsync(Issue(IntegrityIssue.Resolved, now.AddDays(-300), now.AddDays(-200), "a.txt"), default);
        await _issues.InsertAsync(Issue(IntegrityIssue.Resolved, now.AddDays(-300), now.AddDays(-10), "b.txt"), default);
        await _issues.InsertAsync(Issue(IntegrityIssue.AutoRepaired, now.AddDays(-200), now.AddDays(-200), "c.txt"), default);
        await File.WriteAllTextAsync(_dir.Combine("d.txt"), "x");
        await _issues.InsertAsync(Issue(IntegrityIssue.Open, now.AddDays(-300), null, "d.txt"), default);

        var (_, summary) = await Run();

        Assert.That(await _jobs.GetAsync(oldJob.Id, default), Is.Null);
        Assert.That(await _jobs.GetAsync(recentJob.Id, default), Is.Not.Null);
        Assert.That((await All()).Select(i => (i.ZipPath, i.Status)), Is.EquivalentTo(new[]
        {
            ("b.txt", IntegrityIssue.Resolved), ("d.txt", IntegrityIssue.Open),
        }));
        Assert.That(summary, Does.Contain("\"purgedJobs\":1").And.Contain("\"purgedIssues\":2"));
    }

    [TestCase(ZipUnreadable, IssueSeverity.Error)]
    [TestCase(InfoJsonNewerSchema, IssueSeverity.Warn)]
    [TestCase(EntryUnlisted, IssueSeverity.Warn)]
    [TestCase(HashMismatch, IssueSeverity.Error)]
    [TestCase(DefaultBoxArt, IssueSeverity.Info)]
    [TestCase(WrongLocation, IssueSeverity.Warn)]
    [TestCase(DuplicateGameId, IssueSeverity.Error)]
    [TestCase(DuplicateSha1, IssueSeverity.Warn)]
    [TestCase(DbRowDeleted, IssueSeverity.Info)]
    public void Severity_FollowsTheSpecTable(string type, string severity) => Assert.That(SeverityOf(type), Is.EqualTo(severity));

    /// <summary>An external game: its ZIP lists one ROM by pointer and the file sits beside it (E1).</summary>
    private async Task<string> WriteExternal(string zipPath, string title, string? pointer = null)
    {
        var bytes = RomBytes(++_seed);
        pointer ??= $"{title}.{title}.nsp";
        var game = Game(title, Rom($"{title}.nsp", bytes) with { ExternalFileName = pointer }) with { System = ExternalTestHandler.SystemId };
        var path = Abs(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, pointer), bytes);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>()));
        Touch(zipPath);
        return Path.Combine(Path.GetDirectoryName(path)!, pointer);
    }

    /// <summary>Open issues other than the cover codes these image-less games always get.</summary>
    private async Task<List<(string Type, string? ZipPath, string? Entry)>> OpenExternal() =>
        [.. (await Open()).Where(i => i.Type != MissingBoxArt)];

    [Test]
    public async Task External_QuickReportsMissingAndWrongLengthFiles_OfUnopenedAndUnreadableZips()
    {
        var alpha = await WriteExternal("ext/A/Alpha.zip", "Alpha");
        var beta = await WriteExternal("ext/B/Beta.zip", "Beta");
        var gamma = await WriteExternal("ext/G/Gamma.zip", "Gamma");
        await Run();
        Assert.That(await OpenExternal(), Is.Empty, "a healthy external library has no findings");

        File.Delete(alpha);
        File.AppendAllText(beta, "x");
        File.Delete(gamma);
        File.WriteAllText(Abs("ext/G/Gamma.zip"), "not a ZIP");
        Touch("ext/G/Gamma.zip");
        await Run();

        var open = await OpenExternal();
        Assert.That(open, Does.Contain((EntryMissing, (string?)"ext/A/Alpha.zip", (string?)"Alpha.Alpha.nsp")), "unopened ZIP");
        Assert.That(open, Does.Contain((EntrySizeMismatch, (string?)"ext/B/Beta.zip", (string?)"Beta.Beta.nsp")));
        Assert.That(open, Does.Contain((ZipUnreadable, (string?)"ext/G/Gamma.zip", (string?)null)));
        Assert.That(open, Does.Contain((EntryMissing, (string?)"ext/G/Gamma.zip", (string?)"Gamma.Gamma.nsp")), "unreadable ZIP");
    }

    [Test]
    public async Task External_StrayRomsEntry_IsUnlisted()
    {
        await WriteExternal("ext/A/Alpha.zip", "Alpha");
        Damage("ext/A/Alpha.zip", zip => Replace(zip, LibraryZip.RomEntry("Alpha.nsp"), [1, 2, 3]));

        await Run(JobMode.Deep);

        Assert.That(await OpenExternal(), Is.EqualTo(new[] { (EntryUnlisted, (string?)"ext/A/Alpha.zip", (string?)"roms/Alpha.nsp") }));
    }

    [Test]
    public async Task External_ListedFilesAreNotOrphans_UnlistedFilesAndTempsAre()
    {
        var alpha = await WriteExternal("ext/A/Alpha.zip", "Alpha");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(alpha)!, "stray.nsp"), "?");
        File.WriteAllText(alpha + ".tmp", "?");

        await Run();

        Assert.That((await OpenExternal()).Select(i => (i.Type, i.ZipPath)), Is.EquivalentTo(new[]
        {
            (OrphanFile, (string?)"ext/A/stray.nsp"),
            (OrphanFile, (string?)"ext/A/Alpha.Alpha.nsp.tmp"),
        }));
    }

    /// <summary>Another file at <paramref name="path"/> with the same length and different bytes.</summary>
    private static void ReplaceSameSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.Delete(path);
        File.WriteAllBytes(path, bytes);
    }

    [Test]
    public async Task External_ConflictingClaims_AreMissingForBothOwners_AndAreNotHashed()
    {
        await WriteExternal("ext/A/Alpha.zip", "Alpha", pointer: "Shared.nsp");
        await WriteExternal("ext/A/Ace.zip", "Ace", pointer: "shared.NSP");
        var hashed = new List<string>();

        await Run(JobMode.Deep, hooks: new IntegrityHooks { BeforeExternalHash = hashed.Add });

        var missing = (await OpenExternal()).Where(i => i.Type == EntryMissing).Select(i => i.ZipPath).ToList();
        Assert.That(missing, Is.EquivalentTo(new[] { "ext/A/Ace.zip", "ext/A/Alpha.zip" }));
        Assert.That(hashed, Is.Empty);
        Assert.That((await OpenExternal()).Select(i => i.Type), Does.Not.Contain(HashMismatch));
    }

    [Test]
    public async Task External_Deep_HashesAWrongLengthFile()
    {
        var alpha = await WriteExternal("ext/A/Alpha.zip", "Alpha");
        File.AppendAllText(alpha, "x");

        await Run(JobMode.Deep);

        Assert.That((await OpenExternal()).Select(i => (i.Type, i.Entry)), Is.EquivalentTo(new[]
        {
            (EntrySizeMismatch, (string?)"Alpha.Alpha.nsp"),
            (HashMismatch, (string?)"Alpha.Alpha.nsp"),
        }));
    }

    [Test]
    public async Task External_FileGoneAfterItsStat_IsMissing()
    {
        await WriteExternal("ext/A/Alpha.zip", "Alpha");

        await Run(JobMode.Deep, hooks: new IntegrityHooks { BeforeExternalHash = File.Delete });

        Assert.That(await OpenExternal(), Is.EqualTo(new[] { (EntryMissing, (string?)"ext/A/Alpha.zip", (string?)"Alpha.Alpha.nsp") }));
    }

    [TestCase(true, TestName = "External_FileReplacedWhileChecked_IsDropped_AndTheNextIsChecked before the open")]
    [TestCase(false, TestName = "External_FileReplacedWhileChecked_IsDropped_AndTheNextIsChecked after hashing")]
    public async Task External_FileReplacedWhileChecked_IsDropped_AndTheNextIsChecked(bool beforeOpen)
    {
        var alpha = await WriteExternal("ext/A/Alpha.zip", "Alpha");
        File.AppendAllText(alpha, "x"); // a size and a hash finding, both to be dropped
        var beta = await WriteExternal("ext/B/Beta.zip", "Beta");
        ReplaceSameSize(beta);
        void Swap(string path)
        {
            if (path == alpha)
                ReplaceSameSize(path);
        }
        var hooks = beforeOpen ? new IntegrityHooks { BeforeExternalHash = Swap } : new IntegrityHooks { AfterExternalHash = Swap };

        await Run(JobMode.Deep, hooks: hooks);

        Assert.That(await OpenExternal(), Is.EqualTo(new[] { (HashMismatch, (string?)"ext/B/Beta.zip", (string?)"Beta.Beta.nsp") }));
    }

    [Test]
    public async Task External_CancelledDuringAHash_RecordsEarlierFindings_AndResolvesNothing()
    {
        var alpha = await WriteExternal("ext/A/Alpha.zip", "Alpha");
        var beta = await WriteExternal("ext/B/Beta.zip", "Beta");
        await File.WriteAllTextAsync(_dir.Combine("old.txt"), "x");
        await Run();
        File.Delete(_dir.Combine("old.txt"));
        File.Delete(alpha);
        using var cancel = new CancellationTokenSource();

        var (_, summary) = await Run(JobMode.Deep, cancel.Token, new IntegrityHooks
        {
            BeforeExternalHash = path =>
            {
                if (path == beta)
                    cancel.Cancel();
            },
        });

        Assert.That(summary, Does.Contain("\"resolved\":0"));
        Assert.That(await OpenExternal(), Is.EquivalentTo(new[]
        {
            (OrphanFile, (string?)"old.txt", (string?)null),
            (EntryMissing, (string?)"ext/A/Alpha.zip", (string?)"Alpha.Alpha.nsp"),
        }));
    }
}
