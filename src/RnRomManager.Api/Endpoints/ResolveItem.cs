using RnRomManager.Core.Bundles;

namespace RnRomManager.Api.Endpoints;

public sealed record ResolveItem(string? GameId, string? ReleaseSha1, BundleMemberInput? Member);
