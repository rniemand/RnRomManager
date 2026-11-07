namespace RnRomManager.Core.Configuration;

/// <summary><c>Paths</c> section (spec §4, §5).</summary>
public sealed class PathsOptions
{
    public const string Section = "Paths";

    public string Roms { get; set; } = "/app/roms";
    public string Ingest { get; set; } = "/app/ingest";
    public string Logs { get; set; } = "/app/logs";
    public string Cache { get; set; } = "/app/cache";

    /// <summary>Database backups (B13). Holds password hashes and keys: keep it private.</summary>
    public string Backups { get; set; } = "/app/backups";

    public IReadOnlyList<(string Key, string Path)> All() =>
        [("Paths:Roms", Roms), ("Paths:Ingest", Ingest), ("Paths:Logs", Logs), ("Paths:Cache", Cache), ("Paths:Backups", Backups)];
}
