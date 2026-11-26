using Microsoft.Extensions.Time.Testing;

namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>Each read moves the fake clock forward by <paramref name="delay"/> before returning data.</summary>
internal sealed class SlowStream(Stream inner, FakeTimeProvider clock, TimeSpan delay) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        clock.Advance(delay);
        cancellationToken.ThrowIfCancellationRequested();
        return inner.ReadAsync(buffer, cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
