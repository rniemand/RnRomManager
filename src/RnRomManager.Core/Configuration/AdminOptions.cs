namespace RnRomManager.Core.Configuration;

/// <summary><c>Admin</c> section (spec §4, §9.1).</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Used only for the first admin seed. Never logged.</summary>
    public string InitialPassword { get; set; } = "";
}
