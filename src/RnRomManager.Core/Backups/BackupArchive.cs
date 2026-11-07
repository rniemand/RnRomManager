using System.IO.Compression;
using System.Text.Json;

namespace RnRomManager.Core.Backups;

/// <summary>Reads a backup ZIP's <c>manifest.json</c> (B13, B19).</summary>
public static class BackupArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <exception cref="BackupRestoreException">No manifest, or it can't be read.</exception>
    public static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(BackupManifest.FileName)
            ?? throw new BackupRestoreException($"The backup has no {BackupManifest.FileName}.");
        try
        {
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
            if (manifest is null || manifest.Migrations is null || manifest.Tables is null)
                throw new BackupRestoreException($"The backup's {BackupManifest.FileName} is incomplete.");
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new BackupRestoreException($"The backup's {BackupManifest.FileName} isn't valid: {ex.Message}", ex);
        }
    }
}
