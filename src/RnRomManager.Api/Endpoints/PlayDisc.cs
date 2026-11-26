using System.Text;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Play B2–B3: <c>GET /api/games/{id}/roms/{romId}/play-disc</c>, one PlayStation (or Saturn) disc for the browser emulator. A
/// standalone <c>.bin</c>/<c>.iso</c> streams unchanged; a cue set streams as an uncompressed ZIP holding a playback
/// copy of the cue (<see cref="CueSheet.ForPlayback"/>) and the tracks as <c>track-NN.bin</c>. CHD B4: a standalone
/// <c>.chd</c> (or a PlayStation <c>.img</c>) streams unchanged like an <c>.iso</c>. Every member is opened
/// and checked before the response starts; nothing is written to disk and nothing is audited. PSP B2: a PlayStation
/// Portable release streams its standalone <c>.iso</c>, <c>.cso</c>, <c>.pbp</c> or <c>.chd</c> the same way. Saturn, Sega CD and 3DO take PlayStation's disc
/// shapes; TurboGrafx-CD and PC-FX take their cue sets and a standalone <c>.chd</c> only. All these systems share the same cap (<see cref="MaxBytes"/>).
/// CloneCD B8: a CloneCD disc (<c>.img</c>, <c>.ccd</c>, optional <c>.sub</c>) on any of the five disc systems streams as a ZIP
/// holding a cue generated from the <c>.ccd</c> (<see cref="CloneCdSheet.ForPlayback"/>), the <c>.img</c> and the <c>.sub</c>;
/// the stored <c>.ccd</c> is never sent, since EmulatorJS would boot it instead of the cue. File type gaps D11–D12: a cdrdao TOC
/// set (<see cref="TocSheet.ForPlayback"/>) or an Alcohol 120% MDS disc (<see cref="MdsSheet.ForPlayback"/>) streams the same
/// way, with a cue generated from its <c>.toc</c> or <c>.mds</c>, which is never sent either. Browser play format gaps N1: a
/// standalone Nero <c>.nrg</c> streams as a ZIP of a cue generated from its footer and chunks (<see cref="NrgSheet.ForPlayback"/>)
/// and the image's track data as <c>track-01.bin</c>, without the chunks and footer.
/// </summary>
public static class PlayDisc
{
    public const string SystemId = "psx";

    public const string PspSystemId = "psp";

    public const string SaturnSystemId = "saturn";

    public const string SegaCdSystemId = "segacd";

    public const string ThreeDoSystemId = "3do";

    public const string TurboGrafxCdSystemId = "tgcd";

    /// <summary>PC-FX X5: takes TurboGrafx-CD's disc shapes.</summary>
    public const string PcfxSystemId = "pcfx";

    /// <summary>The inclusive cap on the combined opened lengths a launch may stream.</summary>
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> SupportedSystems = new(StringComparer.Ordinal)
    {
        SystemId, PspSystemId, SaturnSystemId, SegaCdSystemId, ThreeDoSystemId, TurboGrafxCdSystemId, PcfxSystemId,
    };

    public const string InvalidPlayDisc = "InvalidPlayDisc";
    public const string PlayDiscTooLarge = "PlayDiscTooLarge";

    private const int MaxBins = 99;

    public static async Task<IResult> HandleAsync(
        Guid id,
        long romId,
        HttpContext ctx,
        IGameRepository repository,
        IRomRepository roms,
        LibraryFiles files,
        ILoggerFactory loggers)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var cancellationToken = ctx.RequestAborted;
        if (await roms.GetAsync(id, romId, cancellationToken) is not { } rom
            || await repository.GetAsync(id, cancellationToken) is not { } game
            || !SupportedSystems.Contains(game.SystemId) || rom.SystemId != game.SystemId
            || rom.ExternalFileName is not { } mainName)
            return Problems.NotFound(ctx);

        var setFiles = (await roms.ListFilesByGameAsync(id, cancellationToken)).Where(f => f.RomId == romId).OrderBy(f => f.Ordinal).ToList();
        var claims = await roms.ListExternalAsync(cancellationToken);

        var opened = new List<Stream>();
        try
        {
            var budget = new Budget($"The disc is larger than {MaxBytes / (1024 * 1024 * 1024)} GiB.");
            var (problem, disc) = await OpenDiscAsync(ctx, game, rom, mainName, setFiles, claims, files, game.SystemId, "track-", "", opened, budget);
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
    /// Multi-disc M4: <c>GET /api/games/{id}/roms/{romId}/play-disc-set</c>, every disc of the PlayStation (Saturn/Sega CD S3: or Saturn or Sega CD) set whose Disc 1
    /// is <paramref name="romId"/> as one uncompressed ZIP: the generated <c>.m3u</c> first, then each disc as
    /// <c>play-disc</c> would send it (cue and TOC tracks and an MDS <c>.mdf</c> aliased <c>disc&lt;n&gt;-track-NN.bin</c>, a CloneCD image <c>disc&lt;n&gt;-track-01.img</c>). One 2 GiB cap covers the set.
    /// Amiga F5, Plus/4 B12: Amiga and Plus/4 floppies are served by <see cref="FloppySetAsync"/> instead.
    /// </summary>
    public static async Task<IResult> HandleSetAsync(
        Guid id,
        long romId,
        HttpContext ctx,
        IGameRepository repository,
        IRomRepository roms,
        LibraryFiles files,
        ILoggerFactory loggers)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var cancellationToken = ctx.RequestAborted;
        if (await repository.GetAsync(id, cancellationToken) is not { } game)
            return Problems.NotFound(ctx);
        if (DiscSets.FloppyExtension(game.SystemId) is { } extension)
            return await FloppySetAsync(game, romId, extension, ctx, roms, files);
        if (!FirmwareNames.TryGetValue(game.SystemId, out var firmware))
            return Problems.NotFound(ctx);
        var romRows = await roms.ListByGameAsync(id, cancellationToken);
        var lookup = (await roms.ListFilesByGameAsync(id, cancellationToken)).ToLookup(f => f.RomId);
        if (DiscSets.Find(game.SystemId, romRows, lookup).FirstOrDefault(s => s.Discs[0].Id == romId) is not { } set
            || set.Discs.Any(d => d.SystemId != game.SystemId || d.ExternalFileName is null))
            return Problems.NotFound(ctx);
        var claims = await roms.ListExternalAsync(cancellationToken);

        var opened = new List<Stream>();
        try
        {
            var budget = new Budget($"The disc set is larger than {MaxBytes / (1024 * 1024 * 1024)} GiB.");
            var entries = new List<PlayDiscEntry>();
            var playlist = new StringBuilder();
            for (var i = 0; i < set.Discs.Count; i++)
            {
                var number = i + 1;
                var rom = set.Discs[i];
                var setFiles = lookup[rom.Id].OrderBy(f => f.Ordinal).ToList();
                var (problem, disc) = await OpenDiscAsync(ctx, game, rom, rom.ExternalFileName!, setFiles, claims, files, game.SystemId,
                    $"disc{number}-track-", $"Disc {number}: ", opened, budget);
                if (problem is null && disc!.Cue is null
                    && firmware.FirstOrDefault(f => f.Equals(disc.Entries[0].Name, StringComparison.OrdinalIgnoreCase)) is { } clash)
                    problem = Invalid(ctx, $"Disc {number}: a disc file can't be named {clash}");
                if (problem is not null)
                {
                    DisposeAll(opened);
                    return problem;
                }
                // S3: Yabause and Genesis Plus GX open a playlist entry as named, so a lone .bin gets the cue EmulatorJS
                // would generate for it alone (createCueFile).
                var cue = disc!.Cue;
                if (cue is null && game.SystemId != SystemId && IsBin(disc.Entries[0].Name))
                {
                    var name = disc.Entries[0].Name;
                    var cueBytes = new UTF8Encoding(false).GetBytes($"FILE \"{name}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
                    cue = new PlayDiscEntry(Path.GetFileNameWithoutExtension(name) + ".cue", new MemoryStream(cueBytes), cueBytes.Length);
                }
                playlist.Append((cue ?? disc.Entries[0]).Name).Append('\n');
                entries.AddRange(cue is null ? disc.Entries : [cue, .. disc.Entries]);
            }

            var bytes = new UTF8Encoding(false).GetBytes(playlist.ToString());
            var head = new PlayDiscEntry(set.Name, new MemoryStream(bytes), bytes.Length);
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

    /// <summary>
    /// Amiga F5, Plus/4 B12: Amiga and Plus/4 floppies: the set whose Disk 1 is <paramref name="romId"/>: the playlist
    /// (<c>Disk 1.adf</c> or <c>Disk 1.d64</c>… one per line, LF-terminated), then each disk's stored bytes as
    /// <c>Disk &lt;n&gt;</c> plus <paramref name="extension"/>. Each disk is read in full first
    /// (<see cref="LibraryFiles.ReadEntry"/>, bounded by its size), so a failure is a problem, never a cut-off 200.
    /// </summary>
    private static async Task<IResult> FloppySetAsync(
        GameRow game,
        long romId,
        string extension,
        HttpContext ctx,
        IRomRepository roms,
        LibraryFiles files)
    {
        var romRows = await roms.ListByGameAsync(game.Id, ctx.RequestAborted);
        var lookup = Array.Empty<RomFileRow>().ToLookup(f => f.RomId);
        if (DiscSets.Find(game.SystemId, romRows, lookup).FirstOrDefault(s => s.Discs[0].Id == romId) is not { } set
            || set.Discs.Any(d => d.SystemId != game.SystemId || d.ExternalFileName is not null))
            return Problems.NotFound(ctx);

        var entries = new List<PlayDiscEntry>();
        var playlist = new StringBuilder();
        for (var i = 0; i < set.Discs.Count; i++)
        {
            var number = i + 1;
            var rom = set.Discs[i];
            if (files.ReadEntry(ctx, game, LibraryZip.RomEntry(rom.FileName), rom.SizeBytes, out var bytes) is { } problem)
                return problem;
            if (bytes!.Length == 0)
                return Invalid(ctx, $"Disk {number}: '{rom.FileName}' is empty");
            var name = FormattableString.Invariant($"Disk {number}{extension}");
            playlist.Append(name).Append('\n');
            entries.Add(new PlayDiscEntry(name, new MemoryStream(bytes), bytes.Length));
        }
        var list = new UTF8Encoding(false).GetBytes(playlist.ToString());
        return new DiscResult("application/zip", new PlayDiscEntry(set.Name, new MemoryStream(list), list.Length), entries);
    }

    /// <summary>
    /// Multi-disc M4 (Saturn/Sega CD S3): the disc systems with sets, and the firmware files the player writes to the
    /// content root, which a standalone disc of that name would overwrite. Amiga and Plus/4 floppies (F5, B12) are aliased, so need none.
    /// </summary>
    private static readonly Dictionary<string, string[]> FirmwareNames = new(StringComparer.Ordinal)
    {
        [SystemId] = ["scph5501.bin"],
        [SaturnSystemId] = ["saturn_bios.bin", "sega_101.bin", "mpr-17933.bin"],
        [SegaCdSystemId] = ["bios_CD_U.bin", "bios_CD_E.bin", "bios_CD_J.bin"],
    };

    /// <summary>
    /// B2: checks and opens one disc's members, adding each opened stream to <paramref name="opened"/> (the caller disposes
    /// them on a problem) and its length to <paramref name="budget"/>. Multi-disc M4: 409 details start with
    /// <paramref name="detailPrefix"/>, and cue tracks are aliased with <paramref name="trackPrefix"/>.
    /// </summary>
    private static async Task<(IResult? Problem, Disc? Disc)> OpenDiscAsync(HttpContext ctx, GameRow game, RomRow rom, string mainName,
        List<RomFileRow> setFiles, IReadOnlyList<ExternalRomRow> claims, LibraryFiles files, string systemId, string trackPrefix,
        string detailPrefix, List<Stream> opened, Budget budget)
    {
        // B2: a FileSets member keeps its exact name, so info.json's fileName equals its externalFileName.
        if (rom.FileName != mainName || setFiles.Any(f => f.FileName != f.ExternalFileName))
            return (Invalid(ctx, detailPrefix + "a disc file's name differs from its external file name"), null);

        var members = new List<Member> { new(mainName, null) };
        members.AddRange(setFiles.Select(f => new Member(f.ExternalFileName, f.Id)));
        if (ShapeProblem(members, systemId) is { } shape)
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

        if (members.Count == 1 && HasExtension(mainName, ".nrg"))
            return await NrgDiscAsync(ctx, mainName, streams[0], trackPrefix, detailPrefix);
        if (members.Count == 1)
            return (null, new Disc(null, [new PlayDiscEntry(mainName, streams[0], streams[0].Length)]));

        if (IsCloneCd(members))
        {
            var sub = members.FirstOrDefault(m => HasExtension(m.Name, ".sub"))?.Name;
            return await SheetDiscAsync(ctx, members, streams, members.FindIndex(m => HasExtension(m.Name, ".ccd")), "the .ccd", detailPrefix,
                (bytes, name) => CloneCdSheet.ForPlayback(bytes, name, members[0].Name, streams[0].Length, sub, trackPrefix));
        }

        // D12: an MDS disc's .mds is members[1], its .mdf the main file.
        if (IsMds(members))
            return await SheetDiscAsync(ctx, members, streams, 1, "the .mds", detailPrefix,
                (bytes, name) => MdsSheet.ForPlayback(bytes, name, members[0].Name, streams[0].Length, trackPrefix));

        var sheetIndex = members.FindIndex(m => IsCue(m.Name) || HasExtension(m.Name, ".toc"));
        var bins = members.Where((_, i) => i != sheetIndex).Select(b => b.Name).ToList();
        return HasExtension(members[sheetIndex].Name, ".toc")
            ? await SheetDiscAsync(ctx, members, streams, sheetIndex, "the .toc", detailPrefix,
                (bytes, name) => TocSheet.ForPlayback(bytes, name, bins, trackPrefix))
            : await SheetDiscAsync(ctx, members, streams, sheetIndex, "the cue", detailPrefix,
                (bytes, name) => CueSheet.ForPlayback(bytes, name, bins, trackPrefix));
    }

    /// <summary>
    /// Play B2, CloneCD B8, D12: reads the set's sheet (cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c>) in full and builds the
    /// playback cue; the archive holds that cue and the aliased tracks, never the stored sheet.
    /// </summary>
    private static async Task<(IResult? Problem, Disc? Disc)> SheetDiscAsync(HttpContext ctx, List<Member> members, List<Stream> streams,
        int sheetIndex, string sheetLabel, string detailPrefix, Func<byte[], string, PlaybackCue> build)
    {
        var sheet = streams[sheetIndex];
        if (sheet.Length > IngestSetPlanner.MaxCueBytes)
            return (Invalid(ctx, $"{detailPrefix}{sheetLabel} is larger than {IngestSetPlanner.MaxCueBytes} bytes"), null);
        var bytes = new byte[sheet.Length];
        await sheet.ReadExactlyAsync(bytes, ctx.RequestAborted);
        var playback = build(bytes, members[sheetIndex].Name);
        if (!playback.IsValid)
            return (Invalid(ctx, detailPrefix + playback.Error!), null);

        var byName = members.Select((m, i) => (m.Name, Stream: streams[i])).ToDictionary(m => m.Name, m => m.Stream, StringComparer.Ordinal);
        List<PlayDiscEntry> entries = [.. playback.Tracks.Select(t => new PlayDiscEntry(t.Alias, byName[t.StoredName], byName[t.StoredName].Length))];
        DisposeAll([sheet]); // read in full; the archive carries the playback cue instead
        return (null, new Disc(new PlayDiscEntry(playback.CueName, new MemoryStream(playback.Content), playback.Content.Length), entries));
    }

    /// <summary>
    /// Browser play format gaps N1–N3: reads a standalone <c>.nrg</c>'s footer and chunk region (bounded by
    /// <see cref="IngestSetPlanner.MaxCueBytes"/>) and sends a generated cue and the image's track data as
    /// <c>&lt;trackPrefix&gt;01.bin</c>, cut at the last track's end so the chunks and footer are never sent.
    /// </summary>
    private static async Task<(IResult? Problem, Disc? Disc)> NrgDiscAsync(HttpContext ctx, string name, Stream image,
        string trackPrefix, string detailPrefix)
    {
        var length = image.Length;
        var tail = new byte[Math.Min(12, length)];
        image.Seek(length - tail.Length, SeekOrigin.Begin);
        await image.ReadExactlyAsync(tail, ctx.RequestAborted);
        if (NrgSheet.Footer(tail, length) is not { } footer)
            return (Invalid(ctx, detailPrefix + "the .nrg has no Nero footer"), null);
        var (chunkStart, regionEnd, v2) = footer;
        if (regionEnd - chunkStart > IngestSetPlanner.MaxCueBytes)
            return (Invalid(ctx, $"{detailPrefix}the .nrg's track list is larger than {IngestSetPlanner.MaxCueBytes} bytes"), null);

        var region = new byte[regionEnd - chunkStart];
        image.Seek(chunkStart, SeekOrigin.Begin);
        await image.ReadExactlyAsync(region, ctx.RequestAborted);
        image.Seek(0, SeekOrigin.Begin);
        var (cue, dataLength) = NrgSheet.ForPlayback(region, chunkStart, v2, name, trackPrefix);
        if (!cue.IsValid)
            return (Invalid(ctx, detailPrefix + cue.Error!), null);
        return (null, new Disc(new PlayDiscEntry(cue.CueName, new MemoryStream(cue.Content), cue.Content.Length),
            [new PlayDiscEntry(cue.Tracks[0].Alias, image, dataLength)]));
    }

    /// <summary>D11: exactly an <c>.mdf</c> (the main file) and an <c>.mds</c> of the same stem, ignoring case.</summary>
    private static bool IsMds(List<Member> members) =>
        members is [var mdf, var mds] && HasExtension(mdf.Name, ".mdf") && HasExtension(mds.Name, ".mds")
        && Path.GetFileNameWithoutExtension(mdf.Name).Equals(Path.GetFileNameWithoutExtension(mds.Name), StringComparison.OrdinalIgnoreCase);

    /// <summary>CloneCD B8: the main file is an <c>.img</c>, beside exactly one <c>.ccd</c> and at most one <c>.sub</c>, all one stem ignoring case.</summary>
    private static bool IsCloneCd(List<Member> members)
    {
        var rest = members.Skip(1).ToList();
        var stem = Path.GetFileNameWithoutExtension(members[0].Name);
        return HasExtension(members[0].Name, ".img") && rest.Count is 1 or 2
            && rest.Count(m => HasExtension(m.Name, ".ccd")) == 1 && rest.Count(m => HasExtension(m.Name, ".sub")) == rest.Count - 1
            && rest.All(m => Path.GetFileNameWithoutExtension(m.Name).Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExtension(string name, string extension) => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsCue(string name) => name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase);

    private static bool IsBin(string name) => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// B2: a standalone <c>.bin</c>/<c>.iso</c> (CHD B4: or <c>.chd</c>; or a PlayStation <c>.img</c>, <c>.pbp</c> or <c>.cbn</c>), or one cue with 1–99 BINs (the main file among
    /// them), all portable names that are unique ignoring case. PSP B2: exactly one standalone <c>.iso</c>, <c>.cso</c>, <c>.pbp</c> or <c>.chd</c>.
    /// TurboGrafx-CD B4: Beetle PCE boots only a cue or a <c>.chd</c> as a CD (a bare <c>.bin</c> would run as a HuCard), so
    /// a single file must be a <c>.chd</c>. CloneCD B8: or a CloneCD disc on any of the five disc systems (<see cref="IsCloneCd"/>).
    /// D11: or an MDS disc (<see cref="IsMds"/>), and a <c>.toc</c> takes the cue's place in a set. Browser play format gaps
    /// N1: or a standalone <c>.nrg</c> (<see cref="NrgDiscAsync"/>). PC-FX X5: the same for Beetle PC-FX, which opens only
    /// <c>cue|ccd|toc|chd</c>.
    /// </summary>
    private static string? ShapeProblem(List<Member> members, string systemId)
    {
        var psp = systemId == PspSystemId;
        if (members.FirstOrDefault(m => CueSheet.NameProblem(m.Name) is not null) is { } bad)
            return $"'{bad.Name}' is not a portable file name";
        if (psp)
            return members is [var umd] && Path.GetExtension(umd.Name).ToLowerInvariant() is ".iso" or ".cso" or ".pbp" or ".chd"
                ? null : "a PSP disc must be a single .iso, .cso, .pbp or .chd";
        if (members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count)
            return "two disc files have the same name ignoring case";
        if (members.Count == 1 && systemId is TurboGrafxCdSystemId or PcfxSystemId)
            return members[0].Name.EndsWith(".chd", StringComparison.OrdinalIgnoreCase)
                ? null : $"a {(systemId == PcfxSystemId ? "PC-FX" : "TurboGrafx-CD")} disc must be a .cue with its .bin tracks, or a .chd";
        if (members.Count == 1)
            return IsBin(members[0].Name) || members[0].Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".chd", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".pbp", StringComparison.OrdinalIgnoreCase)
                || members[0].Name.EndsWith(".cbn", StringComparison.OrdinalIgnoreCase)
                || HasExtension(members[0].Name, ".nrg")
                ? null : "a single-file disc must be a .bin, .iso, .chd, .img, .pbp, .cbn or .nrg";
        if (IsCloneCd(members) || IsMds(members))
            return null;
        var cues = members.Count(m => IsCue(m.Name) || HasExtension(m.Name, ".toc"));
        var bins = members.Count(m => IsBin(m.Name));
        if (cues != 1 || bins != members.Count - 1 || !IsBin(members[0].Name))
            return "a disc set must be one .cue or .toc and its .bin tracks, the main file among them";
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
    /// B2: copies exactly <paramref name="length"/> bytes (the length measured when the file was opened): bytes the
    /// file gained since are left out, and an <see cref="IOException"/> is thrown if it shrank.
    /// </summary>
    public static async Task CopyMeasuredAsync(
        Stream source,
        long length,
        Stream target,
        CancellationToken cancellationToken)
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
