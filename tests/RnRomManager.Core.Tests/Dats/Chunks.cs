namespace RnRomManager.Core.Tests.Dats;

/// <summary>A body without a length, served in two chunks; honours the read's token.</summary>
internal sealed class Chunks(byte[] data, Action? afterFirstChunk = null) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var half = Math.Max(1, data.Length / 2);
        var n = Math.Min(buffer.Length, Math.Min(data.Length - _position, _position == 0 ? half : data.Length));
        data.AsSpan(_position, n).CopyTo(buffer);
        var first = _position == 0;
        _position += n;
        if (first)
            afterFirstChunk?.Invoke();
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
