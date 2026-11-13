namespace RnRomManager.Core.Systems;

public sealed class SystemRegistry : ISystemRegistry
{
    private readonly Dictionary<string, ISystemHandler> _byId;

    /// <exception cref="InvalidOperationException">
    /// Two handlers share an <c>Id</c>, a family mixes embedded and external ROM storage or <c>FileSets</c> values, or a
    /// handler has <c>FileSets</c> with embedded storage (B5).
    /// </exception>
    public SystemRegistry(IEnumerable<ISystemHandler> handlers)
    {
        All = handlers.ToList();
        var duplicate = All.GroupBy(h => h.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"System handler id '{duplicate.Key}' is registered more than once.");
        var mixed = All.GroupBy(h => h.Family).FirstOrDefault(g => g.Select(h => h.EmbedRomsInZip).Distinct().Count() > 1);
        if (mixed is not null)
            throw new InvalidOperationException($"System family '{mixed.Key}' mixes handlers with embedded and external ROM storage.");
        if (All.FirstOrDefault(h => h.FileSets && h.EmbedRomsInZip) is { } embeddedSets)
            throw new InvalidOperationException($"System '{embeddedSets.Id}' has FileSets with embedded ROM storage; file sets need external storage.");
        var mixedSets = All.GroupBy(h => h.Family).FirstOrDefault(g => g.Select(h => h.FileSets).Distinct().Count() > 1);
        if (mixedSets is not null)
            throw new InvalidOperationException($"System family '{mixedSets.Key}' mixes handlers with and without FileSets.");
        _byId = All.ToDictionary(h => h.Id);
    }

    public IReadOnlyList<ISystemHandler> All { get; }

    public ISystemHandler? ById(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<ISystemHandler> ByFamily(string family) => All.Where(h => h.Family == family).ToList();

    public IReadOnlyList<ISystemHandler> ByExtension(string extension) =>
        All.Where(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
}
