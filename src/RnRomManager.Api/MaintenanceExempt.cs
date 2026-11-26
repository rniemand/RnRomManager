namespace RnRomManager.Api;

/// <summary>B27.4: endpoint metadata for long-lived streams that don't hold a live restore back (they read files, not tables).</summary>
public sealed class MaintenanceExempt
{
    public static readonly MaintenanceExempt Instance = new();
}
