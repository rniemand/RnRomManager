using RnRomManager.Api.Http;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// One opened ROM attachment (Bundle Creator B6): the stream, which owns everything it reads from (the ZIP for an
/// embedded entry, the file handle for an external one), the length it will produce, the canonical attachment name and
/// whether the route serves byte ranges. Disposing it closes the stream and what it owns.
/// </summary>
public sealed class LibraryAttachment(Stream stream, long length, string fileName, bool ranges) : IAsyncDisposable, IDisposable
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

/// <summary>
/// The shared open/check logic of the single-file ROM routes (<c>/roms/{romId}/download</c> and
/// <c>/roms/{romId}/files/{fileId}/download</c>): the same lookups, 404s, claims check and canonical names, so another
/// route (the bundle ticket stream) sends exactly what they send.
/// </summary>
public static class LibraryDownload
{
    /// <summary>A release's main file, or the problem the normal route answers.</summary>
    public static async Task<(LibraryAttachment? Attachment, IResult? Problem)> OpenReleaseAsync(HttpContext ctx, Guid gameId, long romId,
        IGameRepository games, IRomRepository roms, LibraryFiles files)
    {
        if (await roms.GetAsync(gameId, romId, ctx.RequestAborted) is not { } rom
            || await games.GetAsync(gameId, ctx.RequestAborted) is not { } game)
            return (null, Problems.NotFound(ctx));
        if (rom.ExternalFileName is { } externalFileName)
        {
            // shortcut: reads every external claim per download; add a per-folder query if this shows up in profiles.
            var claims = await roms.ListExternalAsync(ctx.RequestAborted);
            return files.OpenExternal(ctx, game, externalFileName, claims, out var file) is { } externalProblem
                ? (null, externalProblem)
                : (new LibraryAttachment(file!, file!.Length, rom.FileName, ranges: true), null);
        }
        return files.OpenEntry(ctx, game, LibraryZip.RomEntry(rom.FileName), rom.SizeBytes, out var stream) is { } problem
            ? (null, problem)
            : (new LibraryAttachment(stream!, rom.SizeBytes, rom.FileName, ranges: false), null);
    }

    /// <summary>B20: one file of a set, found by the release it belongs to (another release's file id is a 404).</summary>
    public static async Task<(LibraryAttachment? Attachment, IResult? Problem)> OpenSetFileAsync(HttpContext ctx, Guid gameId, long romId,
        long fileId, IGameRepository games, IRomRepository roms, LibraryFiles files)
    {
        if (await roms.GetFileAsync(gameId, romId, fileId, ctx.RequestAborted) is not { } setFile
            || await games.GetAsync(gameId, ctx.RequestAborted) is not { } game)
            return (null, Problems.NotFound(ctx));
        var claims = await roms.ListExternalAsync(ctx.RequestAborted);
        return files.OpenExternal(ctx, game, setFile.ExternalFileName, claims, out var file) is { } problem
            ? (null, problem)
            : (new LibraryAttachment(file!, file!.Length, setFile.FileName, ranges: true), null);
    }

    /// <summary>
    /// The normal attachment response: <c>Content-Disposition</c> with both <c>filename</c> and <c>filename*</c>, ranges
    /// when <see cref="LibraryAttachment.Ranges"/>. The attachment is also disposed with the response, should the result
    /// never run.
    /// </summary>
    public static IResult Send(HttpContext ctx, LibraryAttachment attachment)
    {
        ctx.Response.RegisterForDisposeAsync(attachment);
        // A ZIP entry stream can't seek, so Results.Stream sends no length; the recorded size lets clients show progress.
        if (!attachment.Ranges)
            ctx.Response.ContentLength = attachment.Length;
        return Results.Stream(attachment.Stream, "application/octet-stream", attachment.FileName, enableRangeProcessing: attachment.Ranges);
    }
}

/// <summary>A read-only stream that also disposes <paramref name="owner"/> (an embedded entry's ZIP) when it is disposed.</summary>
internal sealed class OwningStream(Stream inner, IDisposable owner) : Stream
{
    private int _disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { inner.Dispose(); }
            finally { owner.Dispose(); }
        }
        base.Dispose(disposing);
    }
}
