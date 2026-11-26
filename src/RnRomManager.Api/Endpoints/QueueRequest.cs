using RnRomManager.Core.Bundles;

namespace RnRomManager.Api.Endpoints;

public sealed record QueueRequest(string? Name, IReadOnlyList<BundleEntryInput?>? Entries, int? Revision);
