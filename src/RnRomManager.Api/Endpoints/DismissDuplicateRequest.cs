using System.Text.Json;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/reports/duplicates/dismiss</c> (RP8): a title group's <c>baseTitle</c> or (D3) an IGDB group's <c>igdbId</c>.</summary>
public sealed record DismissDuplicateRequest(
    string? System,
    string? BaseTitle,
    string? GamesKey,
    JsonElement? IgdbId = null);
