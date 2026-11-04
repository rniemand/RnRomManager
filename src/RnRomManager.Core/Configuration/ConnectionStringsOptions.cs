namespace RnRomManager.Core.Configuration;

/// <summary><c>ConnectionStrings</c> section (spec §4).</summary>
public sealed class ConnectionStringsOptions
{
    public const string Section = "ConnectionStrings";

    public string Default { get; set; } = "";
}
