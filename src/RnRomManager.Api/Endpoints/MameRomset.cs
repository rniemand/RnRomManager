using System.Text.Json.Nodes;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>Arcade P2: <c>GET /api/games/{id}/roms/{romId}/romset</c>'s body for a MAME or FBNeo release.</summary>
public static class MameRomset
{
    /// <summary>How many <c>romof</c> steps are followed: a clone's parent, then a BIOS.</summary>
    public const int MaxSteps = 2;

    public static async Task<object> ResolveAsync(RomRow rom, DatSource source, DatStore? store, IRomRepository roms, CancellationToken ct)
    {
        var header = rom.Header is null ? null : JsonNode.Parse(rom.Header) as JsonObject;
        var setName = header?["setName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(rom.OriginalFileName);
        var fileName = CoreFileName(setName, rom.FileName);
        var selfContained = header?["selfContained"] is JsonValue value && value.TryGetValue<bool>(out var contained) && contained;
        if (store is null)
            return new { setName, fileName, deps = Array.Empty<object>(), missing = Array.Empty<string>(), bios = Array.Empty<string>(), selfContained };

        // The romof chain from the loaded DAT: stops at null, an unknown set, a repeat or after MaxSteps.
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { setName };
        for (var next = store.BySetName(setName)?.RomOf; next is not null && chain.Count < MaxSteps; )
        {
            if (store.BySetName(next) is not { } set || !seen.Add(set.Name))
                break;
            chain.Add(set.Name);
            next = set.RomOf;
        }

        // FBNeo P2: board BIOS sets come from Paths:Bios at play time, never from the library. MAME's stay library deps.
        var bios = source == DatSource.Fbneo ? chain.Where(name => store.BySetName(name)!.IsBios).ToList() : [];
        chain.RemoveAll(bios.Contains);

        var found = await roms.ListBySetNamesAsync(rom.SystemId, chain, ct).ConfigureAwait(false);
        var deps = new List<object>();
        var missing = new List<string>();
        foreach (var name in chain)
        {
            // Rows come primary first, then by id.
            if (found.FirstOrDefault(r => string.Equals(SetNameOf(r), name, StringComparison.OrdinalIgnoreCase)) is { } dep)
                deps.Add(new { setName = name, fileName = CoreFileName(name, dep.FileName), gameId = dep.GameId, romId = dep.Id });
            else
                missing.Add(name);
        }
        return new { setName, fileName, deps, missing = selfContained ? [] : missing, bios, selfContained };
    }

    /// <summary>P2: the name the core sees, <c>&lt;set&gt;.7z</c> for a stored <c>.fbneo7z</c>, else <c>&lt;set&gt;.zip</c>.</summary>
    private static string CoreFileName(string setName, string storedName) =>
        setName + (storedName.EndsWith(".fbneo7z", StringComparison.OrdinalIgnoreCase) ? ".7z" : ".zip");

    private static string? SetNameOf(RomRow row) =>
        row.Header is null ? null : (JsonNode.Parse(row.Header) as JsonObject)?["setName"]?.GetValue<string>();
}
