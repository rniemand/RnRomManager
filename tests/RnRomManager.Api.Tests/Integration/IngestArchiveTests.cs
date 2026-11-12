using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §7.2 step 1a + §7.3 through the real ingest job: archive entries as items, fates, limits, 7z, cancel.</summary>
[Category("Integration")]
public class IngestArchiveTests
{
    private const string Gb = "Nintendo - Game Boy";

    private static readonly byte[] TetrisJ = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] SeaQuest = new SyntheticRomBuilder { Seed = 3, Title = "SEAQUEST" }.Build();
    private static readonly byte[] Kirby = new SyntheticRomBuilder { Seed = 5, Title = "KIRBY" }.Build();
    private static readonly byte[] Unknown = new SyntheticRomBuilder { Seed = 99, Title = "MYSTERY" }.Build();

    private ApiFactory _factory = null!;
    private List<JobItem> _items = null!;
    private IJobItemRepository _itemRepository = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        var cache = _factory.Services.GetRequiredService<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [IngestSeeder.NesDat] = [],
            [IngestSeeder.SnesDat] = [],
            [IngestSeeder.Atari2600Dat] = [],
            [IngestSeeder.ColecoVisionDat] = [],
            [IngestSeeder.GameGearDat] = [],
            [IngestSeeder.MasterSystemDat] = [],
            [IngestSeeder.MegaDriveDat] = [],
            [IngestSeeder.Sega32xDat] = [],
            [IngestSeeder.TurboGrafx16Dat] = [],
            [IngestSeeder.PsxDat] = [],
            [IngestSeeder.PspDat] = [],
            [IngestSeeder.GameCubeDat] = [],
            [IngestSeeder.WiiDat] = [],
            [IngestSeeder.Xbox360Dat] = [],
            [IngestSeeder.SaturnDat] = [],
            [Gb] =
            [
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJ),
                new FixtureGame("seaQuest DSV (USA, Europe)", "seaQuest DSV (USA, Europe).gb", SeaQuest),
                new FixtureGame("Kirby's Dream Land (USA, Europe)", "Kirby's Dream Land (USA, Europe).gb", Kirby),
                new FixtureGame("Seven Zip Demo (World)", TestFixtures.Basic7zEntry, TestFixtures.Basic7zRom()),
            ],
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
        _items = [];
        _itemRepository = Substitute.For<IJobItemRepository>();
        _itemRepository.InsertAsync(Arg.Do<JobItem>(_items.Add), Arg.Any<CancellationToken>()).Returns(1L);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private string Ingest => Path.Combine(_factory.Root, "ingest", "gb");
    private string Roms => Path.Combine(_factory.Root, "roms");

    private string Old(string path)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    private string Zip(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(Ingest, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (entryName, bytes) in entries)
            {
                using var stream = zip.CreateEntry(entryName).Open();
                stream.Write(bytes);
            }
        }
        return Old(path);
    }

    private Task<object?> RunAsync(Guid? jobId = null, CancellationToken ct = default) =>
        ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, _itemRepository)
            .RunAsync(new JobContext(jobId ?? Guid.NewGuid(), new JobProgress()), ct);

    private InfoJsonGame ReadZip(string relative)
    {
        using var zip = LibraryZip.Open(Path.Combine(Roms, relative), _factory.Services.GetRequiredService<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    [Test]
    public async Task AllEntriesIngested_ArchiveDeleted_ItemsNameTheEntries()
    {
        var archive = Zip("pack.zip", ("games/Tetris (Japan).gb", TetrisJ), ("sq.gb", SeaQuest), ("readme.txt", [1, 2]));

        await RunAsync();

        Assert.That(File.Exists(archive), Is.False);
        Assert.That(_items.Select(i => (i.SourcePath, i.ArchiveEntry, i.Outcome)), Is.EquivalentTo(new[]
        {
            (archive, (string?)"games/Tetris (Japan).gb", IngestOutcome.Ingested),
            (archive, (string?)"sq.gb", IngestOutcome.Ingested),
        }));
        var rom = ReadZip("gb/T/Tetris.zip").Roms.Single();
        Assert.That(rom.OriginalFileName, Is.EqualTo("Tetris (Japan).gb"), "the file-name part of the entry");
        Assert.That(rom.Metadata.SourcePath, Is.EqualTo(archive));
    }

    [Test]
    public async Task AnyEntryFailed_ArchiveToFailed_ErrorTxtListsEveryEntry()
    {
        var archive = Zip("mixed.zip", ("Tetris.gb", TetrisJ), ("tiny.gb", new byte[0x100]));

        await RunAsync();

        Assert.That(_items.Select(i => (i.ArchiveEntry, i.Outcome, i.Reason)), Is.EquivalentTo(new[]
        {
            ((string?)"Tetris.gb", IngestOutcome.Ingested, (string?)null),
            ((string?)"tiny.gb", IngestOutcome.Failed, (string?)IngestFailures.TooSmall),
        }));
        var failed = Path.Combine(Ingest, "_failed", "mixed.zip");
        Assert.That(File.Exists(archive), Is.False);
        Assert.That(File.Exists(failed), Is.True);
        Assert.That(File.ReadAllText(failed + ".error.txt"), Does.Contain("Tetris.gb: Ingested").And.Contain("tiny.gb: Failed TooSmall"));
    }

    [Test]
    public async Task AllEntriesDuplicates_ArchiveToDuplicates()
    {
        Zip("first.zip", ("Tetris.gb", TetrisJ));
        await RunAsync();
        var again = Zip("again.zip", ("copy.gb", TetrisJ));

        await RunAsync();

        Assert.That(_items.Last().Outcome, Is.EqualTo(IngestOutcome.Duplicate));
        Assert.That(File.Exists(again), Is.False);
        Assert.That(File.Exists(Path.Combine(Ingest, "_duplicates", "again.zip")), Is.True);
    }

    [Test]
    public async Task AnEntryWaitingForReview_KeepsTheArchive()
    {
        var archive = Zip("keep.zip", ("Tetris.gb", TetrisJ), ("mystery.gb", Unknown));

        await RunAsync();

        Assert.That(File.Exists(archive), Is.True);
        Assert.That(_items.Select(i => i.Outcome), Is.EquivalentTo(new[] { IngestOutcome.Ingested, IngestOutcome.Review }));
    }

    [Test]
    public async Task UnsafeArchive_IsOneFailedItem_MovedToFailed()
    {
        var archive = Zip("evil.zip", ("ok.gb", TetrisJ), ("../escape.gb", SeaQuest));

        await RunAsync();

        var item = _items.Single();
        Assert.That((item.SourcePath, item.ArchiveEntry, item.Outcome, item.Reason),
            Is.EqualTo((archive, (string?)null, IngestOutcome.Failed, (string?)ArchiveFailures.ArchiveUnsafe)));
        Assert.That(File.Exists(Path.Combine(Ingest, "_failed", "evil.zip.error.txt")), Is.True);
        Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "escape.gb")), Is.False);
    }

    [Test]
    public async Task ArchiveWithoutRomEntries_FailsInsteadOfBeingDeleted()
    {
        Zip("docs.zip", ("readme.txt", [1]));

        await RunAsync();

        Assert.That((_items.Single().Outcome, _items.Single().Reason), Is.EqualTo((IngestOutcome.Failed, (string?)IngestFailures.UnsupportedType)));
        Assert.That(File.Exists(Path.Combine(Ingest, "_failed", "docs.zip")), Is.True);
    }

    [Test]
    public async Task SevenZipFixture_IsIngested()
    {
        var archive = Path.Combine(Ingest, "basic.7z");
        File.Copy(TestFixtures.Basic7zPath, archive);
        Old(archive);

        await RunAsync();

        Assert.That(_items.Single().Outcome, Is.EqualTo(IngestOutcome.Ingested));
        Assert.That(ReadZip("gb/S/Seven Zip Demo.zip").Roms.Single().FileName, Is.EqualTo(TestFixtures.Basic7zEntry));
        Assert.That(File.Exists(archive), Is.False);
    }

    [Test]
    public async Task CancelMidArchive_FinishesTheArchive_ThenStops_WorkDirCleaned()
    {
        var archive = Zip("a-pack.zip", ("Tetris.gb", TetrisJ), ("sq.gb", SeaQuest), ("kirby.gb", Kirby));
        var later = Path.Combine(Ingest, "z-later.gb");
        File.WriteAllBytes(later, TetrisJ);
        Old(later);
        using var cts = new CancellationTokenSource();
        _itemRepository.When(r => r.InsertAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>())).Do(_ => cts.Cancel());
        var jobId = Guid.NewGuid();

        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => RunAsync(jobId, cts.Token));

        Assert.That(_items.Select(i => i.ArchiveEntry), Is.EquivalentTo(new[] { "Tetris.gb", "sq.gb", "kirby.gb" }));
        Assert.That(File.Exists(archive), Is.False, "the archive's fate was applied");
        Assert.That(File.Exists(later), Is.True, "the next file wasn't touched");
        Assert.That(Directory.Exists(Path.Combine(_factory.Root, "cache", "work", jobId.ToString())), Is.False);
    }
}

/// <summary>Spec I1: an external system's archive is opened, age-checked and extracted through one checked handle.</summary>
[Category("Integration")]
public class IngestExternalArchiveTests
{
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
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _ = _factory.Services; // start the host: it creates the system folders
        _progress = new JobProgress();
        _items = [];
        _itemRepository = Substitute.For<IJobItemRepository>();
        _itemRepository.InsertAsync(Arg.Do<JobItem>(_items.Add), Arg.Any<CancellationToken>()).Returns(1L);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private string Ingest => Path.Combine(_factory.Root, "ingest", ExternalTestHandler.SystemId);

    private string Zip(string name, TimeSpan? age = null, byte[]? rom = null)
    {
        var path = Path.Combine(Ingest, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("game.nsp").Open();
            stream.Write(rom ?? [1, 2, 3, 4]);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(5)));
        return path;
    }

    private JobProgress _progress = null!;

    private Task<object?> RunAsync(ArchiveExtractor? extractor = null) =>
        ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, _itemRepository,
                extractor ?? new ArchiveExtractor(Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveExtractor>.Instance))
            .RunAsync(new JobContext(Guid.NewGuid(), _progress), default);

    private string Failed(string name = "") => Path.Combine(Ingest, "_failed", name);

    private ArchiveExtractor Extractor(Func<string, Stream> createOutput) =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveExtractor>.Instance) { CreateOutput = createOutput };

    private (string Path, string? Entry, string Outcome, string? Reason)[] Items() =>
        [.. _items.Select(i => (i.SourcePath, i.ArchiveEntry, i.Outcome, i.Reason))];

    [Test]
    public async Task OutputFailure_UnsafeSource_AndFreshArchive_EachGetOneItem_AndStayInPlace()
    {
        var first = Zip("a.zip");
        var second = Zip("b.zip");
        var fresh = Zip("c.zip", TimeSpan.Zero);
        var extractor = Extractor(_ =>
        {
            // While a.zip is extracted: b.zip, already listed, becomes a folder; then the copy itself fails.
            File.Delete(second);
            Directory.CreateDirectory(second);
            throw new IOException("disk full");
        });

        await RunAsync(extractor);

        Assert.That(Items(), Is.EqualTo(new[]
        {
            (first, (string?)null, IngestOutcome.Error, (string?)IngestErrors.IoError),
            (second, null, IngestOutcome.Error, IngestErrors.IoError),
            (fresh, null, IngestOutcome.Skipped, IngestReason.TooRecent),
        }));
        Assert.That(_items[0].Details, Does.Contain("disk full"));
        Assert.That(File.Exists(first) && File.Exists(fresh) && Directory.Exists(second), Is.True, "nothing was moved or deleted");
        Assert.That(Directory.GetFileSystemEntries(Path.Combine(Ingest, "_failed")), Is.Empty);
    }

    [Test]
    public async Task ArchiveHeldExclusively_IsOneIoError()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Linux has no sharing modes to deny the open with.");
        var archive = Zip("a.zip");
        using var held = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None);

        await RunAsync(Extractor(_ => throw new InvalidOperationException("must not extract")));

        Assert.That(Items(), Is.EqualTo(new[] { (archive, (string?)null, IngestOutcome.Error, (string?)IngestErrors.IoError) }));
    }

    [Test]
    public async Task ArchiveReplacedDuringExtraction_IsTooRecent_AndNoEntryIsProcessed()
    {
        var archive = Zip("a.zip");
        var bytes = File.ReadAllBytes(archive);
        var extractor = Extractor(path =>
        {
            File.Move(archive, archive + ".old"); // the open handle keeps reading the old file
            File.WriteAllBytes(archive, bytes);
            File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(-5));
            return new FileStream(path, FileMode.CreateNew);
        });

        await RunAsync(extractor);

        Assert.That(Items(), Is.EqualTo(new[] { (archive, (string?)null, IngestOutcome.Skipped, (string?)IngestReason.TooRecent) }));
        Assert.That(File.Exists(archive), Is.True);
    }

    [Test]
    public async Task CorruptArchive_IsStillArchiveCorrupt()
    {
        var archive = Path.Combine(Ingest, "junk.zip");
        File.WriteAllBytes(archive, [1, 2, 3, 4, 5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(-5));

        await RunAsync(Extractor(_ => throw new InvalidOperationException("must not extract")));

        Assert.That(Items(), Is.EqualTo(new[] { (archive, (string?)null, IngestOutcome.Failed, (string?)ArchiveFailures.ArchiveCorrupt) }));
        Assert.That(File.Exists(archive), Is.False);
        Assert.That(File.ReadAllText(Failed("junk.zip.error.txt")), Does.StartWith("reason: ArchiveCorrupt\n"));
    }

    // --- disposition (an empty entry is Failed TooSmall before anything is hashed) ---

    [Test]
    public async Task EveryEntryFailed_MovesTheArchiveToFailed()
    {
        var archive = Zip("a.zip", rom: []);

        await RunAsync();

        Assert.That(Items(), Is.EqualTo(new[] { (archive, (string?)"game.nsp", IngestOutcome.Failed, (string?)IngestFailures.TooSmall) }));
        Assert.That(File.Exists(archive), Is.False);
        Assert.That(File.ReadAllText(Failed("a.zip.error.txt")), Does.Contain("game.nsp: Failed TooSmall"));
    }

    [Test]
    public async Task ArchiveReplacedWhileItsEntriesWereProcessed_IsPreserved_WithOneMoreItem()
    {
        var archive = Zip("a.zip", rom: []);
        var bytes = File.ReadAllBytes(archive);
        var replaced = false;
        _itemRepository.When(r => r.InsertAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>())).Do(_ =>
        {
            if (replaced)
                return;
            replaced = true;
            File.Move(archive, archive + ".old");
            File.WriteAllBytes(archive, bytes);
        });

        await RunAsync();

        Assert.That(Items(), Is.EqualTo(new[]
        {
            (archive, (string?)"game.nsp", IngestOutcome.Failed, (string?)IngestFailures.TooSmall),
            (archive, null, IngestOutcome.Skipped, IngestReason.TooRecent),
        }));
        Assert.That(File.Exists(archive), Is.True);
        Assert.That(Directory.GetFileSystemEntries(Failed()), Is.Empty, "nothing was moved, so nothing was audited");
    }

    [Test]
    public async Task FailedDisposition_IsOneArchiveLevelIoError_EntryOutcomesAndProgressUnchanged()
    {
        var archive = Zip("a.zip", rom: []);
        var outside = Directory.CreateDirectory(Path.Combine(_factory.Root, "outside")).FullName;
        Directory.Delete(Failed());
        Assert.That(TestLinks.TryLinkDirectory(Failed(), outside), Is.True, "this account can't create a directory link");
        try
        {
            await RunAsync();
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(Failed()); // or the factory's cleanup would follow it
        }

        Assert.That(Items(), Is.EqualTo(new[]
        {
            (archive, (string?)"game.nsp", IngestOutcome.Failed, (string?)IngestFailures.TooSmall),
            (archive, null, IngestOutcome.Error, IngestErrors.IoError),
        }));
        Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
        Assert.That(File.Exists(archive), Is.True);
        Assert.That((_progress.Current, _progress.Total), Is.EqualTo((1, 1)), "the archive is still one file");
    }

    [Test]
    public async Task WorkCleanupFailure_KeepsTheOutcome()
    {
        var archive = Zip("a.zip", rom: []);
        FileStream? held = null;
        var extractor = Extractor(path =>
        {
            // A file in the work folder that can't be deleted while it is held (Windows; elsewhere cleanup just works).
            held = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "held.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return new FileStream(path, FileMode.CreateNew);
        });

        try
        {
            var counts = (Dictionary<string, int>)(await RunAsync(extractor))!;

            Assert.That(counts[IngestOutcome.Failed], Is.EqualTo(1));
            Assert.That(File.Exists(Failed("a.zip")), Is.True);
        }
        finally
        {
            held?.Dispose();
        }
    }
}
