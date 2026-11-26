namespace RnRomManager.Api.Tests.Integration;

/// <summary>A body that claims a length without holding it in memory: zeros, produced as read.</summary>
internal sealed class ZeroStream(long length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, length - _position);
        Array.Clear(buffer, offset, n);
        _position += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
