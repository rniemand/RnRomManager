namespace RnRomManager.Api.Endpoints;

/// <param name="CoreSetting">The operator-core setting this system also needs, if any.</param>
public sealed record BiosInfoGroup(
    string System,
    Need Need,
    BiosFile[] Files,
    string Note,
    string? CoreSetting = null);
