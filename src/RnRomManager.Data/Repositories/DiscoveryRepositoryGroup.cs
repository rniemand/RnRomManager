using RnRomManager.Core.Discovery;

namespace RnRomManager.Data.Repositories;

internal sealed record DiscoveryRepositoryGroup(DiscoveryGroup Info, byte[] Norm, Guid[] GameIds);
