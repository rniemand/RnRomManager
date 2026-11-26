namespace RnRomManager.Api.Endpoints;

/// <summary>U1 body: <c>{ "url": "…", "type": "BoxArt" | "Snap" }</c>.</summary>
public sealed record ImageUrlRequest(string? Url, string? Type);
