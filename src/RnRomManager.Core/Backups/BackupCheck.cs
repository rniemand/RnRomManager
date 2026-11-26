using System.IO.Compression;

namespace RnRomManager.Core.Backups;

/// <summary>
/// The restore rules shared by the startup restorer (B19) and the live restore (B27): a bare file name (rule 1) and the
/// pre-drop checks (rule 3), all run before anything is dropped.
/// </summary>
public static class BackupCheck
{
    /// <summary>Rule 1: a file name without any folder part.</summary>
    public static bool IsBareName(string name) =>
        !(name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)
          || Path.GetFileName(name) != name || Path.IsPathRooted(name));

    /// <summary>
    /// Rule 3: opens <paramref name="stream"/> as a backup ZIP and checks its manifest (format version and migrations this
    /// app knows) and that the schema and every listed table's data entry are there.
    /// </summary>
    public static BackupInspection Open(Stream stream, string name, IReadOnlySet<string> knownMigrations)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            return new(null, null, BackupCheckFailure.Invalid, $"Backup {name} isn't a readable ZIP: {ex.Message}");
        }

        var failure = Check(zip, name, knownMigrations, out var manifest);
        if (failure is null)
            return new(zip, manifest, null, null);
        zip.Dispose();
        return new(null, null, failure.Value.Failure, failure.Value.Message);
    }

    private static (BackupCheckFailure Failure, string Message)? Check(ZipArchive zip, string name, IReadOnlySet<string> known,
        out BackupManifest? manifest)
    {
        manifest = null;
        try
        {
            manifest = BackupArchive.ReadManifest(zip);
        }
        catch (BackupRestoreException ex)
        {
            return (BackupCheckFailure.Invalid, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return (BackupCheckFailure.Invalid, $"Backup {name} isn't a readable ZIP: {ex.Message}");
        }

        var unknown = manifest.Migrations.Where(m => !known.Contains(m)).ToList();
        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion || unknown.Count > 0)
        {
            var detail = unknown.Count > 0
                ? $"unknown migrations {string.Join(", ", unknown)}"
                : $"format version {manifest.FormatVersion}";
            return (BackupCheckFailure.TooNew, $"Backup {name} is from a newer app version ({detail}, app {AppInfo.Version}); it can't be restored.");
        }

        // The entries IBackupStore.RestoreAsync reads, so a missing one is refused before the drop.
        foreach (var entry in manifest.Tables.Select(t => BackupManifest.DataEntry(t.Name)).Prepend(BackupManifest.SchemaFileName))
        {
            if (zip.GetEntry(entry) is null)
                return (BackupCheckFailure.Invalid, $"The backup has no {entry}.");
        }
        return null;
    }
}
