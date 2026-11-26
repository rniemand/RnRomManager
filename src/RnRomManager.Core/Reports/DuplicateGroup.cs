namespace RnRomManager.Core.Reports;

/// <summary>
/// RP6: games in one system that own releases with the same base title, or (D1) share <see cref="IgdbId"/>, when
/// <see cref="BaseTitle"/> is the smallest display title. <c>Games</c> are in suggested-target order.
/// </summary>
public sealed record DuplicateGroup(
    string SystemId,
    string BaseTitle,
    string GamesKey,
    IReadOnlyList<DuplicateGame> Games,
    int? IgdbId = null);
