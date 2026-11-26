namespace RnRomManager.Core.Dats;

/// <summary>Only the upstream fields used; unknown ones (<c>size</c>, <c>version</c>, …) are ignored.</summary>
internal sealed record UpstreamEntry(
    string? Id,
    string? Name,
    string? Publisher,
    string? Developer,
    long? ReleaseDate,
    IReadOnlyList<string?>? Category,
    IReadOnlyList<string?>? Languages,
    string? IconUrl,
    string? FrontBoxArt,
    IReadOnlyList<string?>? Screenshots);
