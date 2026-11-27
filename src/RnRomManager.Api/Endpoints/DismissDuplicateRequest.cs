using System.Text.Json;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>POST /api/reports/duplicates/dismiss</c> (RP8): a title group's <c>baseTitle</c>, (D3) an IGDB group's <c>igdbId</c>
/// or (D4) a hash group's <c>hash</c> (<c>sha1:&lt;40 hex&gt;</c> / <c>md5:&lt;32 hex&gt;</c>).
/// </summary>
public sealed record DismissDuplicateRequest(
    string? System,
    string? BaseTitle,
    string? GamesKey,
    JsonElement? IgdbId = null,
    string? Hash = null);
