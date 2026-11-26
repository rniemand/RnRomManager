namespace RnRomManager.Core.Dats;

/// <summary>One kept titledb entry, as the compact file stores it (spec T2). <see cref="Name"/> is titledb's own name.</summary>
public sealed record TitleDbRow(
    string TitleId,
    string Name,
    string? Publisher,
    string? Developer,
    int? ReleaseYear,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Languages,
    string? BoxArtUrl,
    string? SnapUrl);
