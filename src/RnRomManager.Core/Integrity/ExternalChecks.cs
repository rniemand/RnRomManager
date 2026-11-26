using RnRomManager.Core.Library;

namespace RnRomManager.Core.Integrity;

/// <summary>The external file rows, claims and per-pointer outcomes of one run (G1, G2a, G4).</summary>
internal sealed class ExternalChecks(
    ILookup<string, ExternalRomRow> byZip,
    IReadOnlyDictionary<string, List<Guid>> claimants,
    HashSet<string> skippedBySync,
    IReadOnlyList<string> notChecked)
{
    /// <summary>At or below a path DB sync could not open or enter (G4).</summary>
    public bool IsUnchecked(string path) => notChecked.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)
        || (path.Length > p.Length && path[p.Length] == '/' && path.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    private readonly Dictionary<(string, string), bool> _outcomes = [];
    private readonly Dictionary<string, HashSet<string>?> _zipPointers = new(StringComparer.OrdinalIgnoreCase);

    public ILookup<string, ExternalRomRow> ByZip => byZip;
    public IReadOnlyDictionary<string, List<Guid>> Claimants => claimants;
    public HashSet<string> SkippedBySync => skippedBySync;

    private static (string, string) Key(string zipPath, string pointer) => (zipPath.ToUpperInvariant(), pointer.ToUpperInvariant());

    /// <summary>Records that the pointer's stat (and, with <paramref name="hashed"/>, its hash) was checked.</summary>
    public void Mark(string zipPath, string pointer, bool hashed) =>
        _outcomes[Key(zipPath, pointer)] = hashed || _outcomes.GetValueOrDefault(Key(zipPath, pointer));

    /// <returns>Null when not checked; else whether it was hashed too.</returns>
    public bool? Outcome(string zipPath, string pointer) =>
        _outcomes.TryGetValue(Key(zipPath, pointer), out var hashed) ? hashed : null;

    public HashSet<string>? ZipPointers(string zipPath, Func<string, HashSet<string>?> read)
    {
        if (!_zipPointers.TryGetValue(zipPath, out var pointers))
            _zipPointers[zipPath] = pointers = read(zipPath);
        return pointers;
    }
}
