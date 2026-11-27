namespace RnRomManager.Data.Repositories;

internal sealed class GroupRow
{
    public string SystemId { get; init; } = "";
    public string BaseTitle { get; init; } = "";
    public string GamesKey { get; init; } = "";
    public string GameIds { get; init; } = "";
    public int? IgdbId { get; init; }
    public string? HashKind { get; init; }
    public string? Hash { get; init; }
}
