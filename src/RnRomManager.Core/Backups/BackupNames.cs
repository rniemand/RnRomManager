using System.Globalization;
using System.Text.RegularExpressions;

namespace RnRomManager.Core.Backups;

/// <summary>Backup file names (B13, B14): <c>rnrommanager-yyyyMMdd-HHmmss[ (n)].zip</c>, UTC.</summary>
public static partial class BackupNames
{
    public const string Prefix = "rnrommanager-";
    public const string Extension = ".zip";
    public const string TempSuffix = ".tmp";
    public const string RestoredSuffix = ".restored";

    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>The name for a backup started at <paramref name="startedAt"/>; <paramref name="n"/> ≥ 2 adds <c> (n)</c>.</summary>
    public static string For(DateTimeOffset startedAt, int n = 1) =>
        Prefix + startedAt.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + (n > 1 ? $" ({n})" : "") + Extension;

    /// <summary>A backup's UTC start time and clash number, or null when the name isn't a backup name.</summary>
    public static (DateTimeOffset CreatedAt, int N)? Parse(string fileName)
    {
        var match = BackupPattern().Match(fileName);
        if (!match.Success) return null;
        var createdAt = DateTime.ParseExact(match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var n = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
        return (new DateTimeOffset(createdAt, TimeSpan.Zero), n);
    }

    public static bool IsBackup(string fileName) => Parse(fileName) is not null;

    /// <summary>A leftover <c>&lt;backup&gt;.zip.tmp</c> from an interrupted run.</summary>
    public static bool IsTemp(string fileName) =>
        fileName.EndsWith(TempSuffix, StringComparison.Ordinal) && IsBackup(fileName[..^TempSuffix.Length]);

    public static string MarkerFor(string fileName) => fileName + RestoredSuffix;

    [GeneratedRegex(@"^rnrommanager-(?<stamp>\d{8}-\d{6})(?: \((?<n>[2-9]|[1-9]\d+)\))?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupPattern();
}
