namespace RnRomManager.Core.Tests.Ingest;

internal sealed class ReviewResolveJobObservedStream(Stream inner, Action observe) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        observe();
        return inner.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
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
