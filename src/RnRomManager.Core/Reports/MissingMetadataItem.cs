namespace RnRomManager.Core.Reports;

/// <param name="Developers">As stored; empty when unknown.</param>
public sealed record MissingMetadataItem(
    Guid Id,
    string DisplayTitle,
    string SystemId,
    int? ReleaseYear,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers);
