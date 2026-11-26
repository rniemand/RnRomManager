namespace RnRomManager.Core.Tests.Media;

/// <summary>A 200 whose body never arrives: <see cref="Reading"/> completes once the body is being read.</summary>
internal sealed class StalledBody : Stream
{
    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reading => _reading.Task;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _reading.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
