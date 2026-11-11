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
    /// E1: <c>&lt;zipBase&gt;.&lt;romFileName&gt;</c>, the external ROM's file next to its ZIP. Over the budget, the
    /// rom name's stem is cut from its end (its extension is kept); when that isn't enough, <paramref name="zipBase"/>
    /// is cut the same way. A name Windows would treat as a device gets a <c>_</c> prefix.
    /// </summary>
    /// <param name="zipBase">The ZIP's name without <c>.zip</c>.</param>
    /// <param name="candidate">1 for the plain name; N ≥ 2 puts <c> (N)</c> before the extension, inside the budget.</param>
    public static string Build(string zipBase, string romFileName, int candidate = 1)
    {
        var extension = Path.GetExtension(romFileName);
        var stem = romFileName[..^extension.Length];
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

    private static int Scalars(string value) => value.EnumerateRunes().Count();

    /// <summary>The longest prefix of at most <paramref name="maxScalars"/> whole characters.</summary>
    private static string CutScalars(string value, int maxScalars)
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
