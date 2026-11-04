namespace RnRomManager.Core.Systems;

public sealed class SystemRegistry : ISystemRegistry
{
    private readonly Dictionary<string, ISystemHandler> _byId;

    /// <exception cref="InvalidOperationException">Two handlers share an <c>Id</c>.</exception>
    public SystemRegistry(IEnumerable<ISystemHandler> handlers)
    {
        All = handlers.ToList();
        var duplicate = All.GroupBy(h => h.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"System handler id '{duplicate.Key}' is registered more than once.");
        _byId = All.ToDictionary(h => h.Id);
    }

    public IReadOnlyList<ISystemHandler> All { get; }

    public ISystemHandler? ById(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<ISystemHandler> ByFamily(string family) => All.Where(h => h.Family == family).ToList();

    public IReadOnlyList<ISystemHandler> ByExtension(string extension) =>
        All.Where(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
}
