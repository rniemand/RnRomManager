using System.Net;
using System.Text.Json;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// What <c>POST /api/review/{id}/resolve</c> came to (B1, B5): a synchronous refusal (<see cref="Status"/> not 202, its
/// problem <c>code</c>), or the finished job's summary <c>code</c>, <c>gameId</c> and <c>errors</c>.
/// </summary>
public sealed record ResolveOutcome(
    HttpStatusCode Status,
    string? Code,
    Guid? GameId,
    JsonElement? Errors,
    Guid? JobId);
