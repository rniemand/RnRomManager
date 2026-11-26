using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>ZIP Compression Z9: the repack job over real temp-dir ZIPs.</summary>
public class ZipRepackJobTests
{
    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private ISystemHandler _gb = null!;
    private ISystemRegistry _systems = null!;
    private ILibraryMirror _mirror = null!;
    private IGameRepository _games = null!;
    private IJobItemRepository _items = null!;
    private IAuditLog _audit = null!;
    private IFreeSpace _freeSpace = null!;
    private IRomStorageSettings _settings = null!;
    private ZipCompression _level;
    private List<GameRow> _rows = null!;
    private int _seed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _gb = TestHandlers.Fake("gb", embedRomsInZip: true);
        var nes = TestHandlers.Fake("nes", embedRomsInZip: true);
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns([_gb, nes]);
        _systems.ById("gb").Returns(_gb);
        _systems.ById("nes").Returns(nes);
        _reader = new InfoJsonReader([], _systems);
        _mirror = Substitute.For<ILibraryMirror>();
        _games = Substitute.For<IGameRepository>();
        _rows = [];
        _games.ListAllAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<GameRow>)_rows);
        _items = Substitute.For<IJobItemRepository>();
        _audit = Substitute.For<IAuditLog>();
        _freeSpace = Substitute.For<IFreeSpace>();
        _freeSpace.AvailableBytes(Arg.Any<string>()).Returns((long?)null);
        _level = ZipCompression.Med;
        _settings = Substitute.For<IRomStorageSettings>();
        _settings.Compression(Arg.Any<string>()).Returns(_ => _level);
        _settings.SetCompressionAsync("gb", Arg.Any<ZipCompression>(), Arg.Any<CancellationToken>())
            .Returns(call => { _level = call.Arg<ZipCompression>(); return Task.CompletedTask; });
        _seed = 0;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private ZipRepackJob Job(ZipCompression? level, string system = "gb") => new(_games, _mirror, _reader, _systems, _items,
        Options.Create(new PathsOptions { Roms = _dir.Path }), _audit, _freeSpace, TimeProvider.System, _settings, NullLogger<ZipRepackJob>.Instance)
    {
        SystemId = system,
        Level = level,
    };

    private async Task<string> Run(ZipCompression? level, CancellationToken ct = default, string system = "gb") =>
        JsonSerializer.Serialize(await Job(level, system).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), ct));

    private static string Summary(int repacked = 0, int error = 0) => JsonSerializer.Serialize(new { repacked, error });

    private string Abs(GameRow row) => _dir.Combine(row.ZipPath.Split('/'));

    /// <summary>A zero-filled (compressible) one-ROM game ZIP, mirrored as a row.</summary>
    private async Task<GameRow> Add(string title, string system = "gb", bool external = false)
    {
        var bytes = new byte[32 * 1024];
        bytes[0] = (byte)++_seed;
        var ext = system == LegacyExternalTestHandler.SystemId ? ".nds" : ".gb";
        var rom = Rom(title + ext, bytes);
        if (external)
            rom = rom with { ExternalFileName = ExternalRomName.Build(title, rom.FileName) };
        var game = Game(title, rom) with { System = system };
        var zipPath = $"{system}/{title[0]}/{title}.zip";
        var path = _dir.Combine(system, title[..1], title + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new Dictionary<string, Func<Stream>>();
        if (external)
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, rom.ExternalFileName!), bytes);
        else
            entries[LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, entries));
        var row = new MirrorEntry(game, zipPath, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)).ToRows().Game;
        _rows.Add(row);
        return row;
    }

    private List<JobItem> Items() => _items.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IJobItemRepository.InsertAsync))
        .Select(c => (JobItem)c.GetArguments()[0]!).ToList();

    private List<AuditEvent> Audited() => _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Record")
        .Select(c => (AuditEvent)c.GetArguments()[0]!).ToList();

    private static Dictionary<string, (long Length, long Compressed, byte[] Bytes)> Entries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (e.Length, e.CompressedLength, buffer.ToArray());
        });
    }

    private static bool AllStored(string path) => Entries(path).Values.All(e => e.Compressed == e.Length);

    [Test]
    public async Task Change_SavesAndAuditsFirst_ThenRewritesEveryGameStored_ByteIdentical()
    {
        var b = await Add("Bravo");
        var a = await Add("Alpha");
        var before = Entries(Abs(a));
        var order = new List<string>();
        _audit.When(x => x.Record(Arg.Any<AuditEvent>())).Do(_ => order.Add($"audit at {_level}"));
        _mirror.ReplaceAsync(Arg.Any<MirrorEntry>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("mirror");
            return Task.CompletedTask;
        });

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(repacked: 2)));

        Assert.That(order, Is.EqualTo(new[] { "audit at Store", "mirror", "mirror" }), "saved, then audited, then repacked");
        var audited = Audited().Single();
        Assert.That((audited.Action, audited.SystemId, audited.Details), Is.EqualTo((AuditActions.ZipCompressionChanged, "gb", "Med → Store")));
        Assert.That(AllStored(Abs(a)) && AllStored(Abs(b)), Is.True);
        var after = Entries(Abs(a));
        Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
        foreach (var (name, entry) in before)
            Assert.That(after[name].Bytes, Is.EqualTo(entry.Bytes), name);
        Assert.That(Items().Select(i => (i.SourcePath, i.Outcome, i.Reason)), Is.EqualTo(new[]
        {
            (AuditPaths.Library(a.ZipPath), ZipRepackJob.Repacked, (string?)null),
            (AuditPaths.Library(b.ZipPath), ZipRepackJob.Repacked, null),
        }), "ZipPath order");
    }

    [Test]
    public async Task Mirror_GetsThePublishedSizeAndMtime()
    {
        var a = await Add("Alpha");
        MirrorEntry? mirrored = null;
        _mirror.ReplaceAsync(Arg.Do<MirrorEntry>(e => mirrored = e), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await Run(ZipCompression.Store);

        var file = new FileInfo(Abs(a));
        Assert.That(mirrored, Is.Not.Null);
        Assert.That((mirrored!.ZipPath, mirrored.ZipSizeBytes), Is.EqualTo((a.ZipPath, file.Length)));
        Assert.That(MirrorEntry.WholeSeconds(mirrored.ZipMtimeUtc), Is.EqualTo(MirrorEntry.WholeSeconds(file.LastWriteTimeUtc)));
    }

    [Test]
    public async Task OnlyTheJobsSystem_IsRepacked()
    {
        await Add("Alpha");
        var other = await Add("Other", "nes");

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(repacked: 1)));
        Assert.That(AllStored(Abs(other)), Is.False);
    }

    [Test]
    public async Task SameValue_SavesNothing_ButStillRepacksEveryGame()
    {
        var a = await Add("Alpha");
        _level = ZipCompression.Store;

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(repacked: 1)));

        await _settings.DidNotReceive().SetCompressionAsync(Arg.Any<string>(), Arg.Any<ZipCompression>(), Arg.Any<CancellationToken>());
        Assert.That(Audited(), Is.Empty);
        Assert.That(AllStored(Abs(a)), Is.True);
    }

    [Test]
    public async Task RepackAgain_SavesNothing_AndRepacks()
    {
        var a = await Add("Alpha");
        _level = ZipCompression.Store;

        Assert.That(await Run(null), Is.EqualTo(Summary(repacked: 1)));

        await _settings.DidNotReceive().SetCompressionAsync(Arg.Any<string>(), Arg.Any<ZipCompression>(), Arg.Any<CancellationToken>());
        Assert.That(AllStored(Abs(a)), Is.True);
    }

    [Test]
    public async Task NoLongerConfigurable_SavesAndRepacksNothing()
    {
        var a = await Add("Alpha");
        var before = File.ReadAllBytes(Abs(a));
        _gb.EmbedRomsInZip.Returns(false);

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary()));

        await _settings.DidNotReceive().SetCompressionAsync(Arg.Any<string>(), Arg.Any<ZipCompression>(), Arg.Any<CancellationToken>());
        Assert.That(Audited(), Is.Empty);
        Assert.That(Items(), Is.Empty);
        Assert.That(File.ReadAllBytes(Abs(a)), Is.EqualTo(before));
    }

    [Test]
    public async Task SaveFailure_FailsTheJob_BeforeAnyRepack()
    {
        var a = await Add("Alpha");
        var before = File.ReadAllBytes(Abs(a));
        _settings.SetCompressionAsync("gb", Arg.Any<ZipCompression>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ZipCompression.Store));

        Assert.That(Audited(), Is.Empty);
        Assert.That(Items(), Is.Empty);
        Assert.That(File.ReadAllBytes(Abs(a)), Is.EqualTo(before));
    }

    [Test]
    public async Task PendingGame_IsStorageMigrationPending()
    {
        var embed = false;
        _settings.Override(LegacyExternalTestHandler.SystemId).Returns(_ => embed);
        var legacy = new LegacyExternalTestHandler(_settings);
        _systems.All.Returns([_gb, legacy]);
        _systems.ById(LegacyExternalTestHandler.SystemId).Returns(legacy);
        await Add("Alpha", LegacyExternalTestHandler.SystemId, external: true);
        embed = true; // moved into the ZIP, this game not yet

        Assert.That(await Run(null, system: LegacyExternalTestHandler.SystemId), Is.EqualTo(Summary(error: 1)));
        Assert.That(Items().Single().Reason, Is.EqualTo("StorageMigrationPending"));
    }

    [Test]
    public async Task MissingOrUnreadableZip_IsZipUnreadable()
    {
        var gone = await Add("Alpha");
        File.Delete(Abs(gone));
        var broken = await Add("Bravo");
        File.WriteAllBytes(Abs(broken), [1, 2, 3]);

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(error: 2)));
        Assert.That(Items().Select(i => i.Reason), Is.EqualTo(new[] { "ZipUnreadable", "ZipUnreadable" }));
    }

    [Test]
    public async Task UncompressedSizeOverFreeSpace_IsInsufficientSpace()
    {
        var a = await Add("Alpha");
        var before = File.ReadAllBytes(Abs(a));
        // Room for the compressed ZIP and the margin, not for the uncompressed entries.
        _freeSpace.AvailableBytes(Arg.Any<string>()).Returns(before.Length + StorageMigrationJob.SpaceMarginBytes + 1024);

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(error: 1)));

        var item = Items().Single();
        Assert.That(item.Reason, Is.EqualTo("InsufficientSpace"));
        Assert.That(item.Details, Does.Contain("bytes needed at").And.Contain("free"));
        Assert.That(File.ReadAllBytes(Abs(a)), Is.EqualTo(before));
    }

    [Test]
    public async Task VerifyFailure_LeavesTheZip()
    {
        var a = await Add("Alpha");
        // Same length, other bytes: the copy no longer matches info.json's SHA-1.
        using (var archive = ZipFile.Open(Abs(a), ZipArchiveMode.Update))
        {
            var name = LibraryZip.RomEntry("Alpha.gb");
            var length = (int)archive.GetEntry(name)!.Length;
            archive.GetEntry(name)!.Delete();
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(Enumerable.Repeat((byte)7, length).ToArray());
        }
        var before = File.ReadAllBytes(Abs(a));

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(error: 1)));
        Assert.That(Items().Single().Reason, Is.EqualTo("VerifyFailed"));
        Assert.That(File.ReadAllBytes(Abs(a)), Is.EqualTo(before));
    }

    [Test]
    public async Task MirrorFailure_IsDbWriteFailed_TheZipIsRepacked()
    {
        var a = await Add("Alpha");
        _mirror.ReplaceAsync(Arg.Any<MirrorEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        Assert.That(await Run(ZipCompression.Store), Is.EqualTo(Summary(error: 1)));
        Assert.That(Items().Single().Reason, Is.EqualTo("DbWriteFailed"));
        Assert.That(AllStored(Abs(a)), Is.True);
    }

    [Test]
    public async Task Cancel_StopsBetweenGames()
    {
        var a = await Add("Alpha");
        var b = await Add("Bravo");
        using var cts = new CancellationTokenSource();
        _mirror.ReplaceAsync(Arg.Any<MirrorEntry>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        });

        Assert.That(await Run(ZipCompression.Store, cts.Token), Is.EqualTo(Summary(repacked: 1)));
        Assert.That((AllStored(Abs(a)), AllStored(Abs(b))), Is.EqualTo((true, false)));
    }

    [Test]
    public async Task NoGames_EndsWithZeroItems()
    {
        Assert.That(await Run(null), Is.EqualTo(Summary()));
        Assert.That(Items(), Is.Empty);
    }
}
