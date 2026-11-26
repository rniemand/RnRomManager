namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>A response body like Kestrel's with synchronous IO off.</summary>
internal sealed class AsyncOnlyTarget : Stream
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
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
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
