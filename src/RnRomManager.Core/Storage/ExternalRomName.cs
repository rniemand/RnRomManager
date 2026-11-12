using System.Globalization;
using System.Text;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Storage;

/// <summary>
/// The names taken in one library folder while a write allocates into it (spec E2), compared ignoring case: the
/// files and folders on disk, the external files the mirror says games in that folder own (on disk or not, so a
/// missing file's name isn't handed to another game), the names the opened destination ZIP lists, and everything
/// this write has allocated so far.
/// </summary>
public sealed class ExternalNameReservations
{
    private readonly HashSet<string> _taken;

    public ExternalNameReservations(IEnumerable<string> taken) => _taken = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">The folder on disk; a missing one has no entries.</param>
    /// <param name="folderPath">The same folder relative to <c>Paths:Roms</c> with <c>/</c> separators, e.g. <c>switch/L</c>.</param>
    /// <param name="claims">Mirrored external ROMs (<c>IRomRepository.ListExternalAsync</c>); those of other folders are ignored.</param>
    /// <param name="listedByDestination">The <c>externalFileName</c>s of the opened destination ZIP, when the mirror may lag behind it.</param>
    public static ExternalNameReservations ForFolder(string folder, string folderPath, IEnumerable<ExternalRomRow> claims,
        IEnumerable<string>? listedByDestination = null)
    {
        var onDisk = Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Select(e => Path.GetFileName(e)!) : [];
        var prefix = folderPath.TrimEnd('/') + "/";
        var claimed = claims
            .Where(c => c.ZipPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && c.ZipPath.IndexOf('/', prefix.Length) < 0)
            .Select(c => c.ExternalFileName);
        return new ExternalNameReservations([.. onDisk, .. claimed, .. listedByDestination ?? []]);
    }

    public bool IsTaken(string name) => _taken.Contains(name);

    public void Reserve(string name) => _taken.Add(name);

    /// <summary>Frees a name this write is about to vacate, e.g. the current name of a file it renames.</summary>
    public void Release(string name) => _taken.Remove(name);
}

/// <summary>
/// Names of external ROM files and of the ZIPs of external games (spec E1, E2), built in one place so ingest, edits
/// and the integrity location check agree. Every name is one path component with a byte budget: a final name is at
/// most <see cref="MaxNameBytes"/> UTF-8 bytes, so its staging name (<see cref="StagingSuffix"/> appended) still fits
/// the <see cref="MaxComponentBytes"/> of ext4 (and NTFS's 255 UTF-16 units). Cuts are made at character
/// boundaries, never inside a surrogate pair or a multi-byte sequence.
/// </summary>
public static class ExternalRomName
{
    public const int MaxNameBytes = 250;
    public const int MaxComponentBytes = 255;
    public const string StagingSuffix = ".tmp";
    public const string RepairBackupSuffix = ".repair.tmp";

    /// <summary>Longest logical rom <c>fileName</c>, in Unicode scalar values: the <c>roms.file_name</c> column.</summary>
    public const int MaxLogicalNameScalars = InfoJsonValidator.MaxExternalRomFileNameScalars;
    private const string ZipExtension = ".zip";

    /// <summary>
    /// E1: <c>&lt;zipBase&gt;.&lt;romFileName&gt;</c>, the external ROM's file next to its ZIP; when the rom name's stem
    /// starts with <paramref name="zipBase"/> and a space (ignoring case), that repeated title is dropped (B13:
    /// <c>Orbit.[0100…][v0].nsp</c>, not <c>Orbit.Orbit [0100…][v0].nsp</c>). Over the budget, the
    /// rom name's stem is cut from its end (its extension is kept); when that isn't enough, <paramref name="zipBase"/>
    /// is cut the same way. A name Windows would treat as a device gets a <c>_</c> prefix.
    /// </summary>
    /// <param name="zipBase">The ZIP's name without <c>.zip</c>.</param>
    /// <param name="candidate">1 for the plain name; N ≥ 2 puts <c> (N)</c> before the extension, inside the budget.</param>
    public static string Build(string zipBase, string romFileName, int candidate = 1)
    {
        var extension = Path.GetExtension(romFileName);
        var stem = romFileName[..^extension.Length];
        if (stem.Length > zipBase.Length && stem.StartsWith(zipBase + " ", StringComparison.OrdinalIgnoreCase)
            && stem[(zipBase.Length + 1)..].TrimStart(' ') is { Length: > 0 } rest)
            stem = rest;
        var tail = Suffix(candidate) + extension;
        var name = Fit(zipBase, stem, tail);
        // The device check reads up to the first dot, which the prefix moves; fit again with it in the budget.
        return FileNameSanitizer.IsReservedDeviceName(name) ? Fit("_" + zipBase, stem, tail) : name;
    }

    private static string Fit(string zipBase, string stem, string tail)
    {
        var fixedBytes = Bytes(".") + Bytes(tail);
        stem = Cut(stem, Math.Max(0, MaxNameBytes - fixedBytes - Bytes(zipBase)));
        if (stem.Length == 0)
            zipBase = Cut(zipBase, Math.Max(0, MaxNameBytes - fixedBytes));
        return zipBase + "." + stem + tail;
    }

    /// <summary>
    /// The first free <see cref="Build"/> candidate; it and its staging name are reserved. Each candidate is rebuilt
    /// and cut with its own suffix before it is checked, so a suffixed name never grows past the budget.
    /// </summary>
    public static string Allocate(string zipBase, string romFileName, ExternalNameReservations reservations) =>
        FirstFree(n => Build(zipBase, romFileName, n), reservations, StagingSuffix);

    /// <summary>
    /// B10: a <c>FileSets</c> system's file keeps <paramref name="name"/> exactly, with no <c>&lt;ZipBase&gt;.</c>
    /// prefix, <c> (N)</c> suffix or cut, because a cue refers to it byte for byte. When it and its staging name are
    /// free (ignoring case) both are reserved; otherwise nothing is.
    /// </summary>
    public static ExactNameStatus AllocateExact(string name, ExternalNameReservations reservations)
    {
        if (Bytes(name) > MaxNameBytes)
            return ExactNameStatus.TooLong;
        if (reservations.IsTaken(name) || reservations.IsTaken(name + StagingSuffix))
            return ExactNameStatus.Taken;
        reservations.Reserve(name);
        reservations.Reserve(name + StagingSuffix);
        return ExactNameStatus.Allocated;
    }

    /// <summary>
    /// The ZIP name without <c>.zip</c> for an external game's title, without a clash suffix: the sanitized title,
    /// cut so that <c>&lt;base&gt;.zip</c> fits the budget. Two titles with the same base (ignoring case) share a
    /// ZIP path, however they differ beyond the cut: see <see cref="SameZipBase"/>.
    /// </summary>
    public static string ZipBase(string title) => ZipBase(title, candidate: 1);

    /// <summary>The ZIP name of an external game: <c>&lt;base&gt;.zip</c>, or <c>&lt;base&gt; (N).zip</c> with the base cut further to fit.</summary>
    public static string ZipName(string title, int candidate = 1) => ZipBase(title, candidate) + Suffix(candidate) + ZipExtension;

    private static string ZipBase(string title, int candidate)
    {
        var tailBytes = Bytes(Suffix(candidate)) + Bytes(ZipExtension);
        var sanitized = FileNameSanitizer.Sanitize(title);
        var cut = Trim(Cut(sanitized, MaxNameBytes - tailBytes));
        // Sanitize prefixes its own reserved names; this adds the aliases it leaves alone and names a cut produced.
        return FileNameSanitizer.IsReservedDeviceName(cut) ? Trim(Cut("_" + sanitized, MaxNameBytes - tailBytes)) : cut;

        // The same clean-up Sanitize applies, since a cut can expose a trailing space or dot.
        static string Trim(string value) => value.TrimEnd('.', ' ');
    }

    /// <summary>True when a title edit keeps the ZIP where it is: both titles give the same budgeted base, ignoring case.</summary>
    public static bool SameZipBase(string oldTitle, string newTitle) =>
        string.Equals(ZipBase(oldTitle), ZipBase(newTitle), StringComparison.OrdinalIgnoreCase);

    /// <summary>The first free <see cref="ZipName"/> candidate; it and the ZIP writer's temp name are reserved.</summary>
    public static string AllocateZipName(string title, ExternalNameReservations reservations) =>
        FirstFree(n => ZipName(title, n), reservations, StagingSuffix);

    /// <summary>
    /// Allocates the ZIP path of an external game (spec E2): the first <see cref="ZipName"/> candidate, in its
    /// bucket folder, that is free by name (<see cref="ExternalNameReservations"/>, with its temp name) and whose
    /// full relative path the database doesn't treat as equal to another game's stored path or to another ZIP on
    /// disk in that folder. That last check is made by the database with the collation of <c>games.zip_path</c>,
    /// so it can pick a suffix even when no two names match ignoring case, e.g. a composed against a decomposed
    /// accent. Call it before any file is moved.
    /// </summary>
    /// <param name="romsRoot"><c>Paths:Roms</c>.</param>
    /// <param name="claims">Mirrored external ROMs (<c>IRomRepository.ListExternalAsync</c>).</param>
    /// <param name="ownGameId">The game being renamed, whose own row is not a clash; null for a new game.</param>
    /// <param name="ownZipPath">That game's current ZIP path, whose file on disk is not a clash.</param>
    /// <returns>The path relative to <paramref name="romsRoot"/>, <c>/</c> separators: <c>&lt;system&gt;/&lt;bucket&gt;/&lt;name&gt;</c>.</returns>
    public static async Task<string> AllocateZipPathAsync(string romsRoot, string systemId, string title, IReadOnlyList<ExternalRomRow> claims,
        IGameRepository games, CancellationToken cancellationToken, Guid? ownGameId = null, string? ownZipPath = null)
    {
        var folders = new Dictionary<string, (ExternalNameReservations Names, List<string> ZipPaths)>(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var name = ZipName(title, n);
            var folderPath = $"{systemId}/{Bucket.For(name)}";
            if (!folders.TryGetValue(folderPath, out var folder))
            {
                var directory = Path.Combine(romsRoot, systemId, Bucket.For(name));
                var onDisk = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory).Select(f => Path.GetFileName(f)!).Where(f => f.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase))
                    : [];
                folder = (ExternalNameReservations.ForFolder(directory, folderPath, claims),
                    onDisk.Select(f => $"{folderPath}/{f}").Where(p => !string.Equals(p, ownZipPath, StringComparison.Ordinal)).ToList());
                folders[folderPath] = folder;
            }

            if (folder.Names.IsTaken(name) || folder.Names.IsTaken(name + StagingSuffix))
                continue;
            var path = $"{folderPath}/{name}";
            var groups = await games.ListZipPathEquivalenceAsync([path, .. folder.ZipPaths], cancellationToken).ConfigureAwait(false);
            var own = groups.First(g => g.Paths[0] == path); // the candidate is the first input, so it leads its group
            if (own.Paths.Count > 1 || own.Existing.Any(e => e.GameId != ownGameId))
                continue;

            folder.Names.Reserve(name);
            folder.Names.Reserve(name + StagingSuffix);
            return path;
        }
    }

    /// <summary>
    /// True when <paramref name="zipName"/> is a name this builder gives <paramref name="title"/>: candidate 1, or
    /// the whole candidate recomputed for a canonical <c> (N)</c> suffix (N ≥ 2, plain decimal, no leading zero).
    /// </summary>
    public static bool IsZipNameFor(string title, string zipName)
    {
        if (string.Equals(zipName, ZipName(title), StringComparison.OrdinalIgnoreCase))
            return true;
        if (!zipName.EndsWith(")" + ZipExtension, StringComparison.OrdinalIgnoreCase))
            return false;
        var open = zipName.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0)
            return false;
        var digits = zipName[(open + 2)..^(ZipExtension.Length + 1)];
        return digits.Length > 0 && digits[0] != '0' && digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 2
            && string.Equals(zipName, ZipName(title, n), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// W3: a free sibling name for a source ZIP's backup, <c>&lt;zip name&gt;.move-backup.tmp</c>, with the name cut
    /// (and <c> (N)</c> added on a clash) so it, its companion record (<see cref="RecordSuffix"/>) and both of their
    /// <see cref="PartialSuffix"/> staging names fit <see cref="MaxComponentBytes"/>. None ends in <c>.zip.tmp</c>, so
    /// crash recovery leaves them alone. All four names are reserved.
    /// </summary>
    public static string AllocateMoveBackup(string zipName, ExternalNameReservations reservations)
    {
        var longest = Bytes(RecordSuffix + PartialSuffix);
        for (var n = 1; ; n++)
        {
            var tail = Suffix(n) + MoveBackupSuffix;
            var name = Cut(zipName, MaxComponentBytes - longest - Bytes(tail)) + tail;
            string[] pair = [name, name + PartialSuffix, name + RecordSuffix, name + RecordSuffix + PartialSuffix];
            if (pair.Any(reservations.IsTaken))
                continue;
            foreach (var taken in pair)
                reservations.Reserve(taken);
            return name;
        }
    }

    /// <summary>W3: the companion record of a move backup: <c>&lt;backup&gt;.json</c>.</summary>
    public const string RecordSuffix = ".json";

    /// <summary>W3: a source ZIP's verified backup during a move: <c>&lt;zip name&gt;.move-backup.tmp</c>.</summary>
    public const string MoveBackupSuffix = ".move-backup.tmp";

    /// <summary>W3: the name a backup is written under until it is verified; an interrupted copy keeps it.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>
    /// A free sibling name for the file a self-repair replaces: <c>&lt;pointer&gt;.repair.tmp</c>, with the pointer
    /// cut (and <c> (N)</c> added on a clash) so the whole name fits <see cref="MaxComponentBytes"/> even for a
    /// pointer that is itself that long. The name is reserved.
    /// </summary>
    public static string AllocateRepairBackup(string externalFileName, ExternalNameReservations reservations) =>
        FirstFree(n =>
        {
            var tail = Suffix(n) + RepairBackupSuffix;
            return Cut(externalFileName, MaxComponentBytes - Bytes(tail)) + tail;
        }, reservations, stagingSuffix: null);

    /// <summary>
    /// The logical <c>fileName</c> of an external ROM (spec E3): the name stored in <c>info.json</c> and
    /// <c>roms.file_name</c>, used for downloads and notes. A free name that already fits and isn't a Windows
    /// device name is kept exactly as given. Otherwise the stem is cut at a character boundary so that the
    /// <c>_</c> device prefix, <paramref name="keepTail"/>, the <c> (N)</c> clash suffix and the extension all fit
    /// <see cref="MaxLogicalNameScalars"/>; each candidate is cut with its own suffix and then checked, so two names
    /// that only collide once cut still get different suffixes. The result is reserved.
    /// </summary>
    /// <param name="taken">The destination game's current and newly allocated logical names, compared ignoring case.</param>
    /// <param name="keepTail">
    /// The end of the stem that must survive a cut, e.g. the generated <c> [TitleId][vN]</c> tags; ignored unless
    /// the stem ends with it.
    /// </param>
    public static string AllocateLogicalName(string fileName, ExternalNameReservations taken, string keepTail = "")
    {
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        if (keepTail.Length > 0 && stem.EndsWith(keepTail, StringComparison.Ordinal))
            stem = stem[..^keepTail.Length];
        else
            keepTail = "";
        var prefix = FileNameSanitizer.IsReservedDeviceName(fileName) ? "_" : "";

        for (var n = 1; ; n++)
        {
            var tail = keepTail + Suffix(n) + extension;
            var name = prefix + CutScalars(stem, Math.Max(0, MaxLogicalNameScalars - Scalars(prefix) - Scalars(tail))) + tail;
            if (taken.IsTaken(name))
                continue;
            taken.Reserve(name);
            return name;
        }
    }

    /// <summary>Longest absolute source path, in Unicode scalar values: the <c>source_path</c> columns (spec I1).</summary>
    public const int MaxSourcePathScalars = 1024;

    /// <summary>
    /// The name a move stages under before it takes <paramref name="name"/>: <c>&lt;name&gt;.tmp</c>, with the name
    /// cut when that would pass <see cref="MaxComponentBytes"/> (a pointer longer than <see cref="MaxNameBytes"/>
    /// that has to be restored exactly).
    /// </summary>
    public static string StagingName(string name) => Cut(name, MaxComponentBytes - Bytes(StagingSuffix)) + StagingSuffix;

    /// <summary>
    /// Where a loose ingest file is put back after a failed placement (spec W6): its original path when that name
    /// and its staging name are free and fit, else a sibling with the original stem (cut to fit) and extension and
    /// the usual <c> (N)</c> clash suffix. Null when no legal name fits; the caller then keeps the copy where it is.
    /// </summary>
    /// <param name="originalPath">The absolute path the file was taken from.</param>
    /// <param name="fitSourcePathColumn">Also keep the whole path within <see cref="MaxSourcePathScalars"/>, for a path a Pending row may be retargeted to.</param>
    public static string? AllocateRollbackPath(string originalPath, bool fitSourcePathColumn)
    {
        var folder = Path.GetDirectoryName(originalPath)!;
        return AllocateSibling(folder, Path.GetFileName(originalPath), companionSuffix: null,
            fitSourcePathColumn ? MaxSourcePathScalars - Scalars(folder) - 1 : int.MaxValue);
    }

    /// <summary>
    /// Where an external system's ingest file goes in a side folder (<c>_failed/</c>, <c>_duplicates/</c>; spec
    /// W6): its own name when free, else with the stem cut and the usual <c> (N)</c> suffix. The name, its staging
    /// name and <c>&lt;name&gt;&lt;companionSuffix&gt;</c> must all be free (ignoring case) and fit a path component.
    /// Null when no legal name fits.
    /// </summary>
    /// <param name="companionSuffix"><c>.error.txt</c> for <c>_failed/</c>: the diagnostic written next to the file.</param>
    public static string? AllocateSidePath(string folder, string fileName, string? companionSuffix = null) =>
        AllocateSibling(folder, fileName, companionSuffix, int.MaxValue);

    private static string? AllocateSibling(string folder, string name, string? companionSuffix, int maxScalars)
    {
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        var taken = new ExternalNameReservations(
            Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Select(e => Path.GetFileName(e)!) : []);
        var maxBytes = Math.Min(MaxNameBytes, MaxComponentBytes - Bytes(companionSuffix ?? ""));
        for (var n = 1; ; n++)
        {
            var tail = Suffix(n) + extension;
            var cut = CutScalars(Cut(stem, Math.Max(0, maxBytes - Bytes(tail))), Math.Max(0, maxScalars - Scalars(tail)));
            var candidate = cut + tail;
            if ((cut.Length == 0 && stem.Length > 0) || Bytes(candidate) > maxBytes || Scalars(candidate) > maxScalars)
                return null;
            if (!taken.IsTaken(candidate) && !taken.IsTaken(StagingName(candidate))
                && (companionSuffix is null || !taken.IsTaken(candidate + companionSuffix)))
                return Path.Combine(folder, candidate);
        }
    }

    internal static int Scalars(string value) => value.EnumerateRunes().Count();

    /// <summary>The longest prefix of at most <paramref name="maxScalars"/> whole characters.</summary>
    internal static string CutScalars(string value, int maxScalars)
    {
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (maxScalars-- <= 0)
                break;
            length += rune.Utf16SequenceLength;
        }
        return value[..length];
    }

    private static string FirstFree(Func<int, string> candidate, ExternalNameReservations reservations, string? stagingSuffix)
    {
        for (var n = 1; ; n++)
        {
            var name = candidate(n);
            if (reservations.IsTaken(name) || (stagingSuffix is not null && reservations.IsTaken(name + stagingSuffix)))
                continue;
            reservations.Reserve(name);
            if (stagingSuffix is not null)
                reservations.Reserve(name + stagingSuffix);
            return name;
        }
    }

    private static string Suffix(int candidate) => candidate > 1 ? $" ({candidate.ToString(CultureInfo.InvariantCulture)})" : "";

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    /// <summary>The longest prefix of whole characters within <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    internal static string Cut(string value, int maxBytes)
    {
        var bytes = 0;
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return value[..length];
    }
}

/// <summary>B10: the outcome of <see cref="ExternalRomName.AllocateExact"/>.</summary>
public enum ExactNameStatus
{
    Allocated,
    /// <summary>The name or its staging name is already used in the folder (on disk, by a claim or by the destination ZIP).</summary>
    Taken,
    /// <summary>Over <see cref="ExternalRomName.MaxNameBytes"/> UTF-8 bytes; it is never cut.</summary>
    TooLong,
}
