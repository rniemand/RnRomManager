namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Play B2: a write-only wrapper for a response body that refuses synchronous IO. Synchronous writes (the ZIP data
/// descriptors .NET 10 writes when an entry closes) are held in memory, up to <see cref="MaxPendingBytes"/>, and sent
/// ahead of the next asynchronous write or flush; the response itself only ever sees asynchronous calls.
/// </summary>
public sealed class AsyncOnlyWriteStream(Stream inner) : Stream
{
    /// <summary>Far above a ZIP's descriptors and central directory for a 99-track disc.</summary>
    public const int MaxPendingBytes = 1024 * 1024;

    private readonly MemoryStream _pending = new();
    private long _position;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>The bytes waiting for the next asynchronous write or flush.</summary>
    public long PendingBytes => _pending.Length;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_pending.Length + buffer.Length > MaxPendingBytes)
            throw new InvalidOperationException($"More than {MaxPendingBytes} bytes were written synchronously.");
        _pending.Write(buffer);
        _position += buffer.Length;
    }

    public override void WriteByte(byte value) => Write([value]);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await DrainAsync(cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken);
        await inner.FlushAsync(cancellationToken);
    }

    /// <summary>Nothing is sent synchronously; pending bytes wait for <see cref="FlushAsync(CancellationToken)"/>.</summary>
    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        if (_pending.Length == 0)
            return;
        await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), cancellationToken);
        _pending.SetLength(0);
    }

    /// <summary>Leaves <c>inner</c> open (the response owns it); pending bytes must have been flushed asynchronously.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _pending.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _pending.Dispose();
        return base.DisposeAsync();
    }
}
