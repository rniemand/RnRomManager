namespace RnRomManager.Core.Configuration;

/// <summary><c>Integrity</c> section (spec §4).</summary>
public sealed class IntegrityOptions
{
    public const string Section = "Integrity";

    /// <summary>Local time <c>HH:mm</c> of the daily quick check; empty = disabled.</summary>
    public string QuickCheckTime { get; set; } = "03:00";
}
