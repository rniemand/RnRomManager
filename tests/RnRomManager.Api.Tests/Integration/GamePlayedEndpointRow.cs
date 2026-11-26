namespace RnRomManager.Api.Tests.Integration;

internal sealed record GamePlayedEndpointRow(
    string Action,
    string? Username,
    string? SystemId,
    Guid? GameId,
    string? Path,
    string? Details);
