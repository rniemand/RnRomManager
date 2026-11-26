namespace RnRomManager.Api.Endpoints;

public sealed record ResolveRequest(IReadOnlyList<ResolveItem?>? Items);
