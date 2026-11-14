using System.IO.Compression;
using System.Text;
using Microsoft.Net.Http.Headers;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Play B2–B3: <c>GET /api/games/{id}/roms/{romId}/play-disc</c>, one PlayStation disc for the browser emulator. A
/// standalone <c>.bin</c>/<c>.iso</c> streams unchanged; a cue set streams as an uncompressed ZIP holding a playback
/// copy of the cue (<see cref="CueSheet.ForPlayback"/>) and the tracks as <c>track-NN.bin</c>. Every member is opened
/// and checked before the response starts; nothing is written to disk and nothing is audited.
/// </summary>
public static class PlayDisc
{
    public const string SystemId = "psx";

    /// <summary>The combined opened lengths a launch may stream: a standard CD, bounded for browser memory.</summary>
    public const long MaxBytes = 1024L * 1024 * 1024;

    public const string InvalidPlayDisc = "InvalidPlayDisc";
    public const string PlayDiscTooLarge = "PlayDiscTooLarge";

    private const int MaxBins = 99;

    public static async Task<IResult> HandleAsync(Guid id, long romId, HttpContext ctx, IGameRepository repository,
        IRomRepository roms, LibraryFiles files, ILoggerFactory loggers)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var cancellationToken = ctx.RequestAborted;
        if (await roms.GetAsync(id, romId, cancellationToken) is not { } rom
            || await repository.GetAsync(id, cancellationToken) is not { } game
            || game.SystemId != SystemId || rom.SystemId != SystemId || rom.ExternalFileName is not { } mainName)
            return Problems.NotFound(ctx);

        var setFiles = (await roms.ListFilesByGameAsync(id, cancellationToken)).Where(f => f.RomId == romId).OrderBy(f => f.Ordinal).ToList();
        var claims = await roms.ListExternalAsync(cancellationToken);

        // B2: a FileSets member keeps its exact name, so info.json's fileName equals its externalFileName.
        if (rom.FileName != mainName || setFiles.Any(f => f.FileName != f.ExternalFileName))
            return Invalid(ctx, "a disc file's name differs from its external file name");

        var members = new List<Member> { new(mainName, null) };
        members.AddRange(setFiles.Select(f => new Member(f.ExternalFileName, f.Id)));
        if (ShapeProblem(members) is { } shape)
            return Invalid(ctx, shape);
        if (members.Any(m => !IsSoleClaim(game, m, claims)))
            return Problems.NotFound(ctx);

        var opened = new List<FileStream>();
        try
        {
            long total = 0;
            foreach (var member in members)
            {
                if (files.OpenExternal(ctx, game, member.Name, claims, out var stream) is { } problem)
                {
                    DisposeAll(opened);
                    return problem;
                }
                opened.Add(stream!);
                ctx.Response.RegisterForDispose(stream!); // owned until the request ends, even if the result never runs
                var length = stream!.Length;
                if (length == 0)
                {
                    DisposeAll(opened);
                    return Invalid(ctx, $"'{member.Name}' is empty");
                }
                total += length;
                if (total > MaxBytes)
                {
                    DisposeAll(opened);
                    return Problems.Create(ctx, StatusCodes.Status409Conflict, PlayDiscTooLarge, "The disc is larger than 1 GiB.");
                }
            }

            if (members.Count == 1)
                return new DiscResult("application/octet-stream", null, [new Entry(mainName, opened[0], opened[0].Length)]);

            var cueIndex = members.FindIndex(m => IsCue(m.Name));
            var cue = opened[cueIndex];
            if (cue.Length > IngestSetPlanner.MaxCueBytes)
            {
                DisposeAll(opened);
                return Invalid(ctx, $"the cue is larger than {IngestSetPlanner.MaxCueBytes} bytes");
            }
            var cueBytes = new byte[cue.Length];
            await cue.ReadExactlyAsync(cueBytes, cancellationToken);
            var bins = members.Where((_, i) => i != cueIndex).ToList();
            var playback = CueSheet.ForPlayback(cueBytes, members[cueIndex].Name, bins.Select(b => b.Name).ToList());
            if (!playback.IsValid)
            {
                DisposeAll(opened);
                return Invalid(ctx, playback.Error!);
            }

            var byName = members.Select((m, i) => (m.Name, Stream: opened[i])).ToDictionary(m => m.Name, m => m.Stream, StringComparer.Ordinal);
            List<Entry> entries = [.. playback.Tracks.Select(t => new Entry(t.Alias, byName[t.StoredName], byName[t.StoredName].Length))];
            DisposeAll([cue]); // read in full; the archive carries the playback copy instead
            return new DiscResult("application/zip", new Entry(playback.CueName, new MemoryStream(playback.Content), playback.Content.Length), entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DisposeAll(opened);
            loggers.CreateLogger(typeof(PlayDisc)).LogError(ex, "Reading the disc of game {GameId} release {RomId} failed", id, romId);
            return Problems.Create(ctx, StatusCodes.Status500InternalServerError, "IoError", "The disc's files can't be read");
        }
        catch
        {
            DisposeAll(opened);
            throw;
        }
    }

    private sealed record Member(string Name, long? FileId);

    private sealed record Entry(string Name, Stream Source, long Length);

    private static bool IsCue(string name) => name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase);

    private static bool IsBin(string name) => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// B2: a standalone <c>.bin</c>/<c>.iso</c>, or one cue with 1–99 BINs (the main file among them), all portable
    /// names that are unique ignoring case.
    /// </summary>
    private static string? ShapeProblem(List<Member> members)
    {
        if (members.FirstOrDefault(m => CueSheet.NameProblem(m.Name) is not null) is { } bad)
            return $"'{bad.Name}' is not a portable file name";
        if (members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count)
            return "two disc files have the same name ignoring case";
        if (members.Count == 1)
            return IsBin(members[0].Name) || members[0].Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                ? null : "a single-file disc must be a .bin or .iso";
        var cues = members.Count(m => IsCue(m.Name));
        var bins = members.Count(m => IsBin(m.Name));
        if (cues != 1 || bins != members.Count - 1 || !IsBin(members[0].Name))
            return "a disc set must be one .cue and its .bin tracks, the main file among them";
        return bins > MaxBins ? $"a disc set has more than {MaxBins} tracks" : null;
    }

    /// <summary>
    /// B2: the member is the only claim on its name (ignoring case) in the game's folder: no other release of the
    /// game, and no other game, claims it. A main file's claim has no file id.
    /// </summary>
    private static bool IsSoleClaim(GameRow game, Member member, IEnumerable<ExternalRomRow> claims)
    {
        var folder = game.ZipPath[..(game.ZipPath.LastIndexOf('/') + 1)];
        var matching = claims.Where(c => string.Equals(c.ExternalFileName, member.Name, StringComparison.OrdinalIgnoreCase)
            && c.ZipPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && c.ZipPath.IndexOf('/', folder.Length) < 0).ToList();
        return matching is [var only] && only.GameId == game.Id && only.FileId == member.FileId && only.ExternalFileName == member.Name;
    }

    private static IResult Invalid(HttpContext ctx, string detail) =>
        Problems.Create(ctx, StatusCodes.Status409Conflict, InvalidPlayDisc, "The disc can't be played: " + detail + ".");

    private static void DisposeAll(IEnumerable<Stream> streams)
    {
        foreach (var stream in streams)
            stream.Dispose();
    }

    /// <summary>
    /// Streams the checked members: the single image as is, or an uncompressed ZIP (UTF-8 names, async writes only).
    /// Each source is copied up to its measured length; a source that ends early aborts the response. Range headers
    /// are ignored: the whole payload is sent.
    /// </summary>
    private sealed class DiscResult(string contentType, Entry? cue, IReadOnlyList<Entry> entries) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            var cancellationToken = httpContext.RequestAborted;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = contentType;
            response.Headers.CacheControl = "no-store";
            response.Headers[HeaderNames.AcceptRanges] = "none";
            try
            {
                if (cue is null)
                {
                    response.ContentLength = entries[0].Length;
                    await CopyAsync(entries[0], response.Body, cancellationToken);
                }
                else
                {
                    // .NET 10 still writes an entry's data descriptor synchronously when the async entry stream is
                    // disposed on an unseekable target, so small synchronous writes are held and sent asynchronously.
                    await using var body = new AsyncOnlyWriteStream(response.Body);
                    await using (var archive = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true,
                        Encoding.UTF8, cancellationToken))
                    {
                        foreach (var entry in (IEnumerable<Entry>)[cue, .. entries])
                        {
                            var zipEntry = archive.CreateEntry(entry.Name, CompressionLevel.NoCompression);
                            await using var target = await zipEntry.OpenAsync(cancellationToken);
                            await CopyAsync(entry, target, cancellationToken);
                        }
                    }
                    await body.FlushAsync(cancellationToken);
                }
                await response.Body.FlushAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                httpContext.RequestServices.GetRequiredService<ILogger<DiscResult>>()
                    .LogError(ex, "Streaming the disc of {Path} failed after the response started", httpContext.Request.Path);
                httpContext.Abort();
            }
            finally
            {
                foreach (var entry in (IEnumerable<Entry>)[.. entries, .. cue is null ? [] : new[] { cue }])
                    await entry.Source.DisposeAsync();
            }
        }

        private static Task CopyAsync(Entry entry, Stream target, CancellationToken cancellationToken) =>
            CopyMeasuredAsync(entry.Source, entry.Length, target, cancellationToken);
    }

    /// <summary>
    /// B2: copies exactly <paramref name="length"/> bytes (the length measured when the file was opened): bytes the
    /// file gained since are left out, and an <see cref="IOException"/> is thrown if it shrank.
    /// </summary>
    public static async Task CopyMeasuredAsync(Stream source, long length, Stream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                throw new IOException($"the file ended {remaining} bytes before its measured length");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }
}
