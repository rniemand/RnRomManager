namespace RnRomManager.Api.Endpoints;

public sealed record AddGamesRequest(IReadOnlyList<Guid>? GameIds);
