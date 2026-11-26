namespace RnRomManager.Api.Endpoints;

/// <summary>
/// One opened ROM attachment (Bundle Creator B6): the stream, which owns everything it reads from (the ZIP for an
/// embedded entry, the file handle for an external one), the length it will produce, the canonical attachment name and
/// whether the route serves byte ranges. Disposing it closes the stream and what it owns.
/// </summary>
public sealed class LibraryAttachment(
    Stream stream,
    long length,
    string fileName,
    bool ranges) : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; } = stream;

    /// <summary>The bytes a full response sends: the external file's length, or an embedded entry's recorded size.</summary>
    public long Length { get; } = length;

    public string FileName { get; } = fileName;

    /// <summary>True for external files (seekable); embedded entries stream once and ignore <c>Range</c>.</summary>
    public bool Ranges { get; } = ranges;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    public void Dispose() => Stream.Dispose();
}
