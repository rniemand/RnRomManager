using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Net.Http.Headers;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Play B2–B3: <c>GET /api/games/{id}/roms/{romId}/play-disc</c>, one PlayStation (or Saturn) disc for the browser emulator. A
/// standalone <c>.bin</c>/<c>.iso</c> streams unchanged; a cue set streams as an uncompressed ZIP holding a playback
/// copy of the cue (<see cref="CueSheet.ForPlayback"/>) and the tracks as <c>track-NN.bin</c>. CHD B4: a standalone
/// <c>.chd</c> (or a PlayStation <c>.img</c>) streams unchanged like an <c>.iso</c>. Every member is opened
/// and checked before the response starts; nothing is written to disk and nothing is audited. PSP B2: a PlayStation
/// Portable release streams its standalone <c>.iso</c>, <c>.cso</c> or <c>.pbp</c> the same way. Saturn, Sega CD and 3DO take PlayStation's disc
/// shapes. All these systems share the same cap (<see cref="MaxBytes"/>).
/// </summary>
public static class PlayDisc
{
    public const string SystemId = "psx";

    public const string PspSystemId = "psp";

    public const string SaturnSystemId = "saturn";

    public const string SegaCdSystemId = "segacd";

    public const string ThreeDoSystemId = "3do";

    /// <summary>The inclusive cap on the combined opened lengths a launch may stream.</summary>
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> SupportedSystems = new(StringComparer.Ordinal)
    {
        SystemId, PspSystemId, SaturnSystemId, SegaCdSystemId, ThreeDoSystemId,
    };

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
            || !SupportedSystems.Contains(game.SystemId) || rom.SystemId != game.SystemId
            || rom.ExternalFileName is not { } mainName)
            return Problems.NotFound(ctx);
        var psp = game.SystemId == PspSystemId;

        var setFiles = (await roms.ListFilesByGameAsync(id, cancellationToken)).Where(f => f.RomId == romId).OrderBy(f => f.Ordinal).ToList();
        var claims = await roms.ListExternalAsync(cancellationToken);

        var opened = new List<Stream>();
        try
        {
            var budget = new Budget($"The disc is larger than {MaxBytes / (1024 * 1024 * 1024)} GiB.");
            var (problem, disc) = await OpenDiscAsync(ctx, game, rom, mainName, setFiles, claims, files, psp, "track-", "", opened, budget);
            if (problem is not null)
            {
                DisposeAll(opened);
                return problem;
            }
            return disc!.Cue is null
                ? new DiscResult("application/octet-stream", null, disc.Entries)
                : new DiscResult("application/zip", disc.Cue, disc.Entries);
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

    /// <summary>
    /// Multi-disc M4: <c>GET /api/games/{id}/roms/{romId}/play-disc-set</c>, every disc of the PlayStation set whose Disc 1
    /// is <paramref name="romId"/> as one uncompressed ZIP: the generated <c>.m3u</c> first, then each disc as
    /// <c>play-disc</c> would send it (cue tracks aliased <c>disc&lt;n&gt;-track-NN.bin</c>). One 2 GiB cap covers the set.
    /// </summary>
    public static async Task<IResult> HandleSetAsync(Guid id, long romId, HttpContext ctx, IGameRepository repository,
        IRomRepository roms, LibraryFiles files, ILoggerFactory loggers)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var cancellationToken = ctx.RequestAborted;
        if (await repository.GetAsync(id, cancellationToken) is not { } game || game.SystemId != SystemId)
            return Problems.NotFound(ctx);
        var romRows = await roms.ListByGameAsync(id, cancellationToken);
        var lookup = (await roms.ListFilesByGameAsync(id, cancellationToken)).ToLookup(f => f.RomId);
        if (DiscSets.Find(SystemId, romRows, lookup).FirstOrDefault(s => s.Discs[0].Id == romId) is not { } set
            || set.Discs.Any(d => d.SystemId != game.SystemId || d.ExternalFileName is null))
            return Problems.NotFound(ctx);
        var claims = await roms.ListExternalAsync(cancellationToken);

        var opened = new List<Stream>();
        try
        {
            var budget = new Budget($"The disc set is larger than {MaxBytes / (1024 * 1024 * 1024)} GiB.");
            var entries = new List<Entry>();
            var playlist = new StringBuilder();
            for (var i = 0; i < set.Discs.Count; i++)
            {
                var number = i + 1;
                var rom = set.Discs[i];
                var setFiles = lookup[rom.Id].OrderBy(f => f.Ordinal).ToList();
                var (problem, disc) = await OpenDiscAsync(ctx, game, rom, rom.ExternalFileName!, setFiles, claims, files, psp: false,
                    $"disc{number}-track-", $"Disc {number}: ", opened, budget);
                if (problem is null && disc!.Cue is null && disc.Entries[0].Name.Equals(PsxBiosName, StringComparison.OrdinalIgnoreCase))
                    problem = Invalid(ctx, $"Disc {number}: a disc file can't be named {PsxBiosName}");
                if (problem is not null)
                {
                    DisposeAll(opened);
                    return problem;
                }
                playlist.Append((disc!.Cue ?? disc.Entries[0]).Name).Append('\n');
                entries.AddRange(disc.Cue is null ? disc.Entries : [disc.Cue, .. disc.Entries]);
            }

            var bytes = new UTF8Encoding(false).GetBytes(playlist.ToString());
            var head = new Entry(set.Name, new MemoryStream(bytes), bytes.Length);
            if (entries.Select(e => e.Name).Append(head.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count + 1)
            {
                DisposeAll(opened);
                return Invalid(ctx, "two entries of the disc set have the same name ignoring case");
            }
            return new DiscResult("application/zip", head, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DisposeAll(opened);
            loggers.CreateLogger(typeof(PlayDisc)).LogError(ex, "Reading the disc set of game {GameId} release {RomId} failed", id, romId);
            return Problems.Create(ctx, StatusCodes.Status500InternalServerError, "IoError", "The disc's files can't be read");
        }
        catch
        {
            DisposeAll(opened);
            throw;
        }
    }

    /// <summary>Multi-disc M4: the firmware the player writes to the content root as <c>/scph5501.bin</c>.</summary>
    private const string PsxBiosName = "scph5501.bin";

    /// <summary>The running total of opened lengths against <see cref="MaxBytes"/>, and the 409 title once it's passed.</summary>
    private sealed class Budget(string tooLargeTitle)
    {
        public long Total { get; set; }

        public string TooLargeTitle { get; } = tooLargeTitle;
    }

    /// <summary>One disc as streamed: a standalone image (no cue), or a playback cue and its aliased tracks.</summary>
    private sealed record Disc(Entry? Cue, IReadOnlyList<Entry> Entries);

    /// <summary>
    /// B2: checks and opens one disc's members, adding each opened stream to <paramref name="opened"/> (the caller disposes
    /// them on a problem) and its length to <paramref name="budget"/>. Multi-disc M4: 409 details start with
    /// <paramref name="detailPrefix"/>, and cue tracks are aliased with <paramref name="trackPrefix"/>.
    /// </summary>
    private static async Task<(IResult? Problem, Disc? Disc)> OpenDiscAsync(HttpContext ctx, GameRow game, RomRow rom, string mainName,
        List<RomFileRow> setFiles, IReadOnlyList<ExternalRomRow> claims, LibraryFiles files, bool psp, string trackPrefix,
        string detailPrefix, List<Stream> opened, Budget budget)
    {
        // B2: a FileSets member keeps its exact name, so info.json's fileName equals its externalFileName.
        if (rom.FileName != mainName || setFiles.Any(f => f.FileName != f.ExternalFileName))
            return (Invalid(ctx, detailPrefix + "a disc file's name differs from its external file name"), null);

        var members = new List<Member> { new(mainName, null) };
        members.AddRange(setFiles.Select(f => new Member(f.ExternalFileName, f.Id)));
        if (ShapeProblem(members, psp) is { } shape)
            return (Invalid(ctx, detailPrefix + shape), null);
        if (members.Any(m => !IsSoleClaim(game, m, claims)))
            return (Problems.NotFound(ctx), null);

        var streams = new List<Stream>();
        foreach (var member in members)
        {
            if (files.OpenExternal(ctx, game, member.Name, claims, out var stream) is { } problem)
                return (problem, null);
            opened.Add(stream!);
            streams.Add(stream!);
            ctx.Response.RegisterForDispose(stream!); // owned until the request ends, even if the result never runs
            var length = stream!.Length;
            if (length == 0)
                return (Invalid(ctx, $"{detailPrefix}'{member.Name}' is empty"), null);
            budget.Total += length;
            if (budget.Total > MaxBytes)
                return (Problems.Create(ctx, StatusCodes.Status409Conflict, PlayDiscTooLarge, budget.TooLargeTitle), null);
        }

        if (members.Count == 1)
            return (null, new Disc(null, [new Entry(mainName, streams[0], streams[0].Length)]));

        var cueIndex = members.FindIndex(m => IsCue(m.Name));
        var cue = streams[cueIndex];
        if (cue.Length > IngestSetPlanner.MaxCueBytes)
            return (Invalid(ctx, $"{detailPrefix}the cue is larger than {IngestSetPlanner.MaxCueBytes} bytes"), null);
        var cueBytes = new byte[cue.Length];
        await cue.ReadExactlyAsync(cueBytes, ctx.RequestAborted);
        var bins = members.Where((_, i) => i != cueIndex).ToList();
        var playback = CueSheet.ForPlayback(cueBytes, members[cueIndex].Name, bins.Select(b => b.Name).ToList(), trackPrefix);
        if (!playback.IsValid)
            return (Invalid(ctx, detailPrefix + playback.Error!), null);

        var byName = members.Select((m, i) => (m.Name, Stream: streams[i])).ToDictionary(m => m.Name, m => m.Stream, StringComparer.Ordinal);
        List<Entry> entries = [.. playback.Tracks.Select(t => new Entry(t.Alias, byName[t.StoredName], byName[t.StoredName].Length))];
        DisposeAll([cue]); // read in full; the archive carries the playback copy instead
        return (null, new Disc(new Entry(playback.CueName, new MemoryStream(playback.Content), playback.Content.Length), entries));
    }

    private sealed record Member(string Name, long? FileId);

    private sealed record Entry(string Name, Stream Source, long Length);

    private static bool IsCue(string name) => name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase);

    private static bool IsBin(string name) => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// B2: a standalone <c>.bin</c>/<c>.iso</c> (CHD B4: or <c>.chd</c>; or a PlayStation <c>.img</c> or <c>.pbp</c>), or one cue with 1–99 BINs (the main file among
    /// them), all portable names that are unique ignoring case. PSP B2: exactly one standalone <c>.iso</c>, <c>.cso</c> or <c>.pbp</c>.
    /// </summary>
    private static string? ShapeProblem(List<Member> members, bool psp)
    {
        if (members.FirstOrDefault(m => CueSheet.NameProblem(m.Name) is not null) is { } bad)
            return $"'{bad.Name}' is not a portable file name";
        if (psp)
            return members is [var umd] && Path.GetExtension(umd.Name).ToLowerInvariant() is ".iso" or ".cso" or ".pbp"
                ? null : "a PSP disc must be a single .iso, .cso or .pbp";
        if (members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count)
            return "two disc files have the same name ignoring case";
        if (members.Count == 1)
            return IsBin(members[0].Name) || members[0].Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".chd", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".pbp", StringComparison.OrdinalIgnoreCase)
                ? null : "a single-file disc must be a .bin, .iso, .chd, .img or .pbp";
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
                    // The ZIP's own length isn't known up front; the payload size lets the player show progress.
                    response.Headers["X-Content-Bytes"] = (cue.Length + entries.Sum(e => e.Length)).ToString(CultureInfo.InvariantCulture);
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
