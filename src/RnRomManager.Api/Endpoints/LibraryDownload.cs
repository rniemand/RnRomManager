using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

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
