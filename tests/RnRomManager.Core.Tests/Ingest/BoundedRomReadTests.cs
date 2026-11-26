using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Nintendo64;
using RnRomManager.Systems.Snes;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class BoundedRomReadTests
{
    private const long MiB = 1024 * 1024;
    private static readonly AuditActor Actor = new(42, "boss", Guid.NewGuid());

    private TempDir _dir = null!;

    [SetUp]
    public void SetUp() => _dir = new TempDir();

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Write(string name, byte[] bytes)
    {
        var path = _dir.Combine(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Seeded(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Test]
    public void OverLimitFile_IsOutOfRange_WithoutAllocatingTheRom()
    {
        var path = _dir.Combine("big.rom");
        using (var file = File.Create(path))
            file.SetLength(64 * MiB + 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = BoundedRomRead.ReadAsync(path, 1, 64 * MiB, null, CancellationToken.None).GetAwaiter().GetResult();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That((result.Status, result.Length, result.Bytes), Is.EqualTo((BoundedReadStatus.OutOfRange, 64 * MiB + 1, (byte[]?)null)));
        Assert.That(allocated, Is.LessThan(MiB));
    }

    [Test]
    public async Task UnderMinimum_IsOutOfRange()
    {
        var result = await BoundedRomRead.ReadAsync(Write("small.rom", [1, 2]), 3, 10, null, default);
        Assert.That((result.Status, result.Length), Is.EqualTo((BoundedReadStatus.OutOfRange, 2L)));
    }

    [Test]
    public async Task InRange_ReadsExactlyTheFile_AndClosesTheHandle()
    {
        var bytes = Seeded(4096);
        var path = Write("ok.rom", bytes);

        var result = await BoundedRomRead.ReadAsync(path, 1, 4096, null, default);

        Assert.That(result.Status, Is.EqualTo(BoundedReadStatus.Read));
        Assert.That(result.Bytes, Is.EqualTo(bytes));
        File.Delete(path); // the handle is closed, so this works on Windows too
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public async Task EarlyEof_IsChanged()
    {
        var result = await BoundedRomRead.ReadAsync(Write("eof.rom", Seeded(4096)), 1, 8192, s => new ChangingStream(s, endAfter: 1000), default);
        Assert.That(result.Status, Is.EqualTo(BoundedReadStatus.Changed));
        Assert.That(result.Details, Does.Contain("ended after 1000 of 4096"));
    }

    [Test]
    public async Task Growth_IsChanged()
    {
        var result = await BoundedRomRead.ReadAsync(Write("grow.rom", Seeded(4096)), 1, 8192, s => new ChangingStream(s, extra: 1), default);
        Assert.That(result.Status, Is.EqualTo(BoundedReadStatus.Changed));
        Assert.That(result.Details, Does.Contain("grew past 4096"));
    }

    [Test]
    public void IdentityNormalization_ReturnsTheSameArray()
    {
        var bytes = Seeded(1024);
        Assert.That(ReferenceEquals(BoundedRomRead.Normalize(new Embedded(), bytes), bytes), Is.True);
    }

    [Test]
    public void SnesCopierStrip_PreservesTheHashAndStoredBytes()
    {
        var image = Seeded(256 * 1024, seed: 2);
        var dump = new byte[SnesCopierHeader.Size + image.Length];
        image.CopyTo(dump, SnesCopierHeader.Size);

        var stored = BoundedRomRead.Normalize(new SnesHandler(), dump);

        Assert.That(stored, Is.EqualTo(image));
        Assert.That(SHA1.HashData(stored), Is.EqualTo(SHA1.HashData(image)));
    }

    [Test]
    public void N64ByteSwap_PreservesTheHashAndStoredBytes()
    {
        var bigEndian = Seeded(8 * 1024, seed: 3);
        bigEndian[0] = 0x80;
        var swapped = new byte[bigEndian.Length];
        for (var i = 0; i < bigEndian.Length; i += 2)
            (swapped[i], swapped[i + 1]) = (bigEndian[i + 1], bigEndian[i]);
        var original = swapped.ToArray();

        var stored = BoundedRomRead.Normalize(new Nintendo64Handler(), swapped);

        Assert.That(stored, Is.EqualTo(bigEndian));
        Assert.That(SHA1.HashData(stored), Is.EqualTo(SHA1.HashData(bigEndian)));
        Assert.That(swapped, Is.EqualTo(original), "the read array is left unchanged");
    }

    [Test]
    public async Task Ingest_ChangedDuringRead_IsIoError_AndLeavesTheFile()
    {
        var (pipeline, paths) = Pipeline();
        var path = Write(Path.Combine("ingest", "gb", "test.gb"), Seeded(512));
        pipeline.WrapEmbeddedReadStream = s => new ChangingStream(s, endAfter: 100);

        var result = await pipeline.ProcessAsync(new IngestFile("gb", path, "test.gb", 512, DateTime.UtcNow.AddMinutes(-1)),
            Guid.NewGuid(), Actor, new CircuitBreaker(), default);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
        Assert.That(File.Exists(path), Is.True);
        Assert.That(Directory.Exists(Path.Combine(paths.Ingest, "gb", "_failed")), Is.False);
    }

    [Test]
    public async Task Ingest_HandleLongerThanTheLimit_IsTooLarge()
    {
        var (pipeline, _) = Pipeline();
        var path = Write(Path.Combine("ingest", "gb", "test.gb"), Seeded(2048));

        // The listing saw an in-range size; the open handle is over the limit.
        var result = await pipeline.ProcessAsync(new IngestFile("gb", path, "test.gb", 512, DateTime.UtcNow.AddMinutes(-1)),
            Guid.NewGuid(), Actor, new CircuitBreaker(), default);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.TooLarge)));
    }

    private (IngestPipeline Pipeline, PathsOptions Paths) Pipeline()
    {
        var paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        Directory.CreateDirectory(Path.Combine(paths.Ingest, "gb"));
        var systems = new SystemRegistry([new Embedded()]);
        var options = Options.Create(paths);
        var pipeline = new IngestPipeline(new DatCache(options, systems, NullLogger<DatCache>.Instance), systems, Substitute.For<IRomRepository>(),
            Substitute.For<IGameRepository>(), Substitute.For<ILibraryMirror>(), Substitute.For<IIntegrityIssueRepository>(),
            Substitute.For<IIngestReviewRepository>(), new InfoJsonReader([], systems), TestMedia.Offline(), options, Substitute.For<IAuditLog>(),
            new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance);
        return (pipeline, paths);
    }
}
