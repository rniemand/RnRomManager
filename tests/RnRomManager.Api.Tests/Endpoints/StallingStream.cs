namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>Every async read (or write) waits until cancelled; <see cref="Stalled"/> completes once one is waiting.</summary>
internal sealed class StallingStream(bool stallReads) : Stream
{
    public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => stallReads;
    public override bool CanSeek => false;
    public override bool CanWrite => !stallReads;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Stalled.TrySetResult();
        await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Stalled.TrySetResult();
        await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
