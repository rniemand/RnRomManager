namespace RnRomManager.Data.Repositories;

internal sealed class Index(List<DiscoveryRepositoryGroup> groups)
{
    public List<DiscoveryRepositoryGroup> Groups { get; } = groups;
    public Dictionary<string, DiscoveryRepositoryGroup> ByKey { get; } = groups.ToDictionary(g => g.Info.Key, StringComparer.Ordinal);
}
