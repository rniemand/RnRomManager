namespace RnRomManager.Api.Endpoints;

/// <summary>Q4 body of <c>POST /api/review/delete</c>.</summary>
public sealed record ReviewDeleteRequest(long[]? Ids);
