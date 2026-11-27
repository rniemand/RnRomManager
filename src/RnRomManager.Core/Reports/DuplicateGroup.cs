namespace RnRomManager.Core.Reports;

/// <summary>
/// RP6: games in one system that own releases with the same base title, or (D1) share <see cref="IgdbId"/>, when
/// <see cref="BaseTitle"/> is the smallest display title, or (D2) share a file hash (<see cref="HashKind"/> <c>sha1</c> or
/// <c>md5</c>, lower-case <see cref="Hash"/>), when it is the smallest matching file name. <c>Games</c> are in
/// suggested-target order.
/// </summary>
public sealed record DuplicateGroup(
    string SystemId,
    string BaseTitle,
    string GamesKey,
    IReadOnlyList<DuplicateGame> Games,
    int? IgdbId = null,
    string? HashKind = null,
    string? Hash = null);
