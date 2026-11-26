namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Simulates a source that changes while it is read: it ends early, or yields bytes past its end.</summary>
internal sealed class ChangingStream(Stream inner, long endAfter = long.MaxValue, int extra = 0) : Stream
{
    private long _position;
    private int _extra = extra;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= endAfter)
            return 0;
        var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, endAfter - _position)]);
        if (n == 0 && _extra > 0 && buffer.Length > 0)
        {
            buffer[0] = 0xFF;
            _extra--;
            n = 1;
        }
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
