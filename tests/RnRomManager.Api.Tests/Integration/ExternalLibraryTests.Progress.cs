using RnRomManager.Core.Audit;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Large ROM import progress B2/B3: an external ROM's hash and copies report job steps.</summary>
public partial class ExternalLibraryTests
{
    /// <summary>Records the job's step on every read, so a test sees it mid-hash or mid-copy.</summary>
    private sealed class ObservedStream(Stream inner, Action observe) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            observe();
            return inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            observe();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static NoReplaceRenameResult Unsupported(string from, string to) => NoReplaceRename.FromErrno(95, from, to);

    private static Task<IngestItemResult?> IngestAsync(IngestPipeline pipeline, IngestFile file, JobProgress progress) =>
        pipeline.ProcessAsync(file, Guid.NewGuid(), new AuditActor(null, null, Guid.NewGuid()), new CircuitBreaker(), CancellationToken.None, progress);

    [Test]
    public async Task Progress_HashingThenCopyingToLibrary_ThenCleared()
    {
        var progress = new JobProgress();
        var seen = new List<JobStepSnapshot?>();
        void Observe() => seen.Add(progress.StepAt(DateTime.UtcNow));
        var hooks = new ExternalIngestHooks
        {
            WrapHashStream = stream => new ObservedStream(stream, Observe),
            Move = new ExternalMoveHooks { Rename = Unsupported, WrapSource = stream => new ObservedStream(stream, Observe) },
        };
        var file = Drop("dump.xci");

        var result = await IngestAsync(Pipeline(hooks), file, progress);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom), "the copy is unchanged");
        Assert.That(seen.Select(s => s?.Phase).Distinct(), Is.EqualTo(new[] { JobStepPhases.Hashing, JobStepPhases.CopyingToLibrary }));
        Assert.That(seen, Has.All.Matches<JobStepSnapshot?>(s => s!.BytesTotal == Rom.Length));
        Assert.That(seen.Where(s => s!.Phase == JobStepPhases.Hashing).Max(s => s!.BytesDone), Is.EqualTo(Rom.Length), "the last read sees the bytes so far");
        Assert.That(seen.Where(s => s!.Phase == JobStepPhases.CopyingToLibrary).Max(s => s!.BytesDone), Is.EqualTo(Rom.Length));
        Assert.That(progress.StepAt(DateTime.UtcNow), Is.Null);
    }

    [Test]
    public async Task Progress_SameVolumeRename_ReportsOnlyHashing()
    {
        var progress = new JobProgress();
        var phases = new List<string?>();
        var hooks = new ExternalIngestHooks { WrapHashStream = stream => new ObservedStream(stream, () => phases.Add(progress.StepAt(DateTime.UtcNow)?.Phase)) };

        var result = await IngestAsync(Pipeline(hooks), Drop("dump.xci"), progress);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(phases.Distinct(), Is.EqualTo(new[] { JobStepPhases.Hashing }));
        Assert.That(progress.StepAt(DateTime.UtcNow), Is.Null);
    }

    [Test]
    public async Task Progress_FailedCopy_StillClearsTheStep()
    {
        var progress = new JobProgress();
        var hooks = new ExternalIngestHooks
        {
            Move = new ExternalMoveHooks { Rename = Unsupported, WrapSource = _ => throw new IOException("disk gone") },
        };
        var file = Drop("dump.xci");

        var result = await IngestAsync(Pipeline(hooks), file, progress);

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Error));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Rom), "the source stays");
        Assert.That(progress.StepAt(DateTime.UtcNow), Is.Null);
    }

    [Test]
    public async Task Progress_DuplicateMovedAsideByCopy_ReportsCopying()
    {
        Assert.That(Code(await IngestAsync(Pipeline(), Drop("dump.xci"))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var progress = new JobProgress();
        var phases = new List<string?>();
        var hooks = new ExternalIngestHooks
        {
            Aside = new ExternalMoveHooks
            {
                Rename = Unsupported,
                WrapSource = stream => new ObservedStream(stream, () => phases.Add(progress.StepAt(DateTime.UtcNow)?.Phase)),
            },
        };

        var result = await IngestAsync(Pipeline(hooks), Drop("again.xci"), progress);

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Duplicate));
        Assert.That(File.ReadAllBytes(Path.Combine(Ingest, StorageFolders.DuplicatesFolder, "again.xci")), Is.EqualTo(Rom));
        Assert.That(phases.Distinct(), Is.EqualTo(new[] { JobStepPhases.Copying }));
        Assert.That(progress.StepAt(DateTime.UtcNow), Is.Null);
    }
}
