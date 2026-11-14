using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>Play B2: a disc member is copied up to the length measured when it was opened.</summary>
public class PlayDiscCopyTests
{
    private static readonly byte[] Bytes = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];

    [Test]
    public async Task CopiesExactlyTheMeasuredLength_LeavingOutLaterGrowth()
    {
        using var target = new MemoryStream();

        await PlayDisc.CopyMeasuredAsync(new MemoryStream(Bytes), 150_000, target, default);

        Assert.That(target.ToArray(), Is.EqualTo(Bytes[..150_000]));
    }

    [Test]
    public void AFileThatShrankAfterOpening_Throws()
    {
        using var target = new MemoryStream();

        Assert.ThrowsAsync<IOException>(() => PlayDisc.CopyMeasuredAsync(new MemoryStream(Bytes[..1000]), 2000, target, default));
        Assert.That(target.Length, Is.EqualTo(1000));
    }

    [Test]
    public void Cancellation_StopsTheCopy()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => PlayDisc.CopyMeasuredAsync(new MemoryStream(Bytes), Bytes.Length, new MemoryStream(), cancelled.Token));
    }

    /// <summary>A response body like Kestrel's with synchronous IO off.</summary>
    private sealed class AsyncOnlyTarget : Stream
    {
        private readonly MemoryStream _written = new();

        public byte[] ToArray() => _written.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("sync write");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("sync write");
        public override void WriteByte(byte value) => throw new InvalidOperationException("sync write");
        public override void Flush() => throw new InvalidOperationException("sync flush");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Test]
    public async Task AsyncOnlyWriteStream_HoldsSyncWrites_AndSendsThemInOrderOnTheNextAsyncCall()
    {
        var target = new AsyncOnlyTarget();
        await using var stream = new AsyncOnlyWriteStream(target);

        await stream.WriteAsync(new byte[] { 1, 2 });
        stream.Write([3, 4]);
        stream.WriteByte(5);
        stream.Flush();
        Assert.That((target.ToArray(), stream.PendingBytes, stream.Position), Is.EqualTo((new byte[] { 1, 2 }, 3L, 5L)));

        await stream.WriteAsync(new byte[] { 6 });
        stream.Write([7]);
        await stream.FlushAsync();

        Assert.That(target.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7 }));
        Assert.That(stream.PendingBytes, Is.Zero);
    }

    [Test]
    public void AsyncOnlyWriteStream_BoundsTheHeldBytes()
    {
        using var stream = new AsyncOnlyWriteStream(new AsyncOnlyTarget());
        stream.Write(new byte[AsyncOnlyWriteStream.MaxPendingBytes]);

        Assert.Throws<InvalidOperationException>(() => stream.WriteByte(0));
    }

    [Test]
    public async Task AZipWrittenThroughIt_NeverWritesSynchronously_AndReadsBack()
    {
        var target = new AsyncOnlyTarget();
        await using (var stream = new AsyncOnlyWriteStream(target))
        {
            await using (var archive = await System.IO.Compression.ZipArchive.CreateAsync(stream, System.IO.Compression.ZipArchiveMode.Create,
                leaveOpen: true, System.Text.Encoding.UTF8))
            {
                foreach (var name in new[] { "Pokémon.cue", "track-01.bin" })
                {
                    var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
                    await using var entryStream = await entry.OpenAsync();
                    await PlayDisc.CopyMeasuredAsync(new MemoryStream(Bytes), Bytes.Length, entryStream, default);
                }
            }
            await stream.FlushAsync();
        }

        using var read = new System.IO.Compression.ZipArchive(new MemoryStream(target.ToArray()));
        Assert.That(read.Entries.Select(e => (e.FullName, e.Length)), Is.EqualTo(new[] { ("Pokémon.cue", (long)Bytes.Length), ("track-01.bin", (long)Bytes.Length) }));
    }
}
