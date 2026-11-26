namespace RnRomManager.Core.Tests.Dats;

/// <summary>A body that never sends a byte, like a connection dropped without a reset.</summary>
internal sealed class StalledStream(TaskCompletionSource reading) : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        reading.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
