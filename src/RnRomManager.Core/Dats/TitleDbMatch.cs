namespace RnRomManager.Core.Dats;

/// <summary>
/// T4/T5: a ROM matched through titledb. <see cref="Base"/> is the base game's entry (its <see cref="GameName"/> is the
/// release's <c>datGameName</c>); the release name, languages and file name come from <paramref name="NameEntry"/>
/// when it has one, else from the base.
/// </summary>
/// <param name="TitleId">The id the file carries, kept in its file name.</param>
/// <param name="NameEntry">The entry whose name the release uses; null when it falls back to the base name.</param>
/// <param name="BaseTitle">T5 grouping key and game title.</param>
/// <param name="RomFileName">T5 rom <c>fileName</c>.</param>
/// <param name="Revision"><c>v&lt;N&gt;</c> when N &gt; 0.</param>
public sealed record TitleDbMatch(
    string TitleId,
    TitleKind Kind,
    TitleDbRow Base,
    TitleDbRow? NameEntry,
    string BaseTitle,
    string GameName,
    string RomFileName,
    string? Revision,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Languages);
