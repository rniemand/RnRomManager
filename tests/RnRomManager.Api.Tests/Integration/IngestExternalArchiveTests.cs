using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec I1: an external system's archive is opened, age-checked and extracted through one checked handle.</summary>
[Category("Integration")]
public class IngestExternalArchiveTests
{
    private ApiFactory _factory = null!;
    private List<JobItem> _items = null!;
    private IJobItemRepository _itemRepository = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>(services),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _progress = new JobProgress();
        _items = [];
        _itemRepository = Substitute.For<IJobItemRepository>();
        _itemRepository.InsertAsync(Arg.Do<JobItem>(_items.Add), Arg.Any<CancellationToken>()).Returns(1L);
    }

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
