namespace RnRomManager.Core.Storage;

/// <summary>
/// Read-only wrapper that throws <see cref="LimitExceededException"/> once more than <c>limit</c> bytes are
/// actually read (spec §7.3, §9.3): declared sizes in archive headers can lie. Disposes the inner stream.
/// </summary>
public sealed class LimitStream(Stream inner, long limit) : Stream
{
    public long Limit { get; } = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(Window(buffer)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(Window(buffer), cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Ask for at most one byte past the limit: enough to detect an overrun without buffering a huge read.
    private Span<byte> Window(Span<byte> buffer) => buffer[..WindowLength(buffer.Length)];

    private Memory<byte> Window(Memory<byte> buffer) => buffer[..WindowLength(buffer.Length)];

    private int WindowLength(int requested) =>
        Limit - BytesRead >= requested ? requested : (int)(Limit - BytesRead) + 1;

    private int Count(int read)
    {
        BytesRead += read;
        return BytesRead > Limit ? throw new LimitExceededException(Limit) : read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
