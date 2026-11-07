namespace RnRomManager.Core.Backups;

/// <summary><c>manifest.json</c> of a backup ZIP (B13).</summary>
public sealed record BackupManifest(
    int FormatVersion,
    Guid BackupId,
    DateTimeOffset CreatedAt,
    string AppVersion,
    string Database,
    IReadOnlyList<string> Migrations,
    IReadOnlyList<BackupTable> Tables)
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "manifest.json";
    public const string SchemaFileName = "schema.sql";
    public const string DataFolder = "data/";

    public static string DataEntry(string table) => DataFolder + table + ".jsonl";
}

public sealed record BackupTable(string Name, long Rows);
