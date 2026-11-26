namespace RnRomManager.Data.Tests.Database;

/// <summary>Runs <paramref name="onRead"/> before every read, honouring the token (large ROM import progress tests).</summary>
internal sealed class IntegrityJobObservedStream(Stream inner, Action onRead) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        onRead();
        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        onRead();
        cancellationToken.ThrowIfCancellationRequested();
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
