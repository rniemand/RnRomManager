using System.Runtime.InteropServices;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>How a <see cref="BoundedRomRead"/> ended.</summary>
public enum BoundedReadStatus
{
    /// <summary>Exactly the handle's length was read into <see cref="BoundedReadResult.Bytes"/>.</summary>
    Read,

    /// <summary>The open handle's length is outside the limits; nothing was allocated.</summary>
    OutOfRange,

    /// <summary>The file ended before the handle's length, or grew past it, while it was read.</summary>
    Changed,
}

/// <param name="Length">The open handle's length.</param>
public sealed record BoundedReadResult(BoundedReadStatus Status, long Length, byte[]? Bytes = null, string? Details = null);

/// <summary>
/// The one whole-ROM read of an embedded source, shared by ingest and Review. It checks the open handle's length
/// against the limits before allocating, reads exactly that length into one array, detects early EOF or growth, and
/// closes the handle before it returns, so the caller can move or delete the source.
/// </summary>
public static class BoundedRomRead
{
    /// <summary>Open or read failures throw (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>).</summary>
    /// <param name="wrap">A test seam: wraps the read stream (the length check uses the real handle).</param>
    public static async Task<BoundedReadResult> ReadAsync(string path, long minBytes, long maxBytes, Func<Stream, Stream>? wrap,
        CancellationToken cancellationToken)
    {
        // bufferSize 0: no FileStream buffer, so an out-of-range file allocates next to nothing.
        await using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous);
        var length = handle.Length;
        if (length < minBytes || length > maxBytes || length > Array.MaxLength)
            return new(BoundedReadStatus.OutOfRange, length);
        var stream = wrap?.Invoke(handle) ?? handle;
        await using var _ = stream == handle ? null : stream;
        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return new(BoundedReadStatus.Changed, length, Details: $"the file ended after {read} of {length} bytes");
            read += n;
        }
        var probe = new byte[1];
        if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
            return new(BoundedReadStatus.Changed, length, Details: $"the file grew past {length} bytes while it was read");
        return new(BoundedReadStatus.Read, length, bytes);
    }

    /// <summary>
    /// Normalizes <paramref name="bytes"/> for hashing and storage. Returns the backing array when normalization returns
    /// all of one (the input itself, or a new array the handler made), otherwise one copy of the slice.
    /// </summary>
    public static byte[] Normalize(ISystemHandler handler, byte[] bytes)
    {
        var normalized = handler.NormalizeForHashing(bytes);
        return MemoryMarshal.TryGetArray(normalized, out var segment) && segment.Array is { } array
            && segment.Offset == 0 && segment.Count == array.Length
            ? array
            : normalized.ToArray();
    }
}
