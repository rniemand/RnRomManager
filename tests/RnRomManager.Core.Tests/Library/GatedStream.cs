namespace RnRomManager.Core.Tests.Library;

/// <summary>Reads only after <c>gate</c> completes, recording that a generation got its slot.</summary>
internal sealed class GatedStream(byte[] bytes, Task gate) : MemoryStream(bytes)
{
    public bool Started { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Started = true;
        await gate;
        return await base.ReadAsync(buffer, cancellationToken);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        Started = true;
        await gate;
        return await base.ReadAsync(buffer, offset, count, cancellationToken);
    }
}
