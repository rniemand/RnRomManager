using System.Text.Json.Nodes;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>Arcade P2: <c>GET /api/games/{id}/roms/{romId}/romset</c>'s body for a MAME release.</summary>
public static class MameRomset
{
    /// <summary>How many <c>romof</c> steps are followed: a clone's parent, then a BIOS.</summary>
    public const int MaxSteps = 2;

    public static async Task<object> ResolveAsync(RomRow rom, DatStore? store, IRomRepository roms, CancellationToken ct)
    {
        var header = rom.Header is null ? null : JsonNode.Parse(rom.Header) as JsonObject;
        var setName = header?["setName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(rom.OriginalFileName);
        if (store is null)
            return new { setName, deps = Array.Empty<object>(), missing = Array.Empty<string>() };

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

        var found = await roms.ListBySetNamesAsync(rom.SystemId, chain, ct).ConfigureAwait(false);
        var deps = new List<object>();
        var missing = new List<string>();
        foreach (var name in chain)
        {
            // Rows come primary first, then by id.
            if (found.FirstOrDefault(r => string.Equals(SetNameOf(r), name, StringComparison.OrdinalIgnoreCase)) is { } dep)
                deps.Add(new { setName = name, gameId = dep.GameId, romId = dep.Id });
            else
                missing.Add(name);
        }
        var selfContained = header?["selfContained"] is JsonValue value && value.TryGetValue<bool>(out var contained) && contained;
        return new { setName, deps, missing = selfContained ? [] : missing };
    }

    private static string? SetNameOf(RomRow row) =>
        row.Header is null ? null : (JsonNode.Parse(row.Header) as JsonObject)?["setName"]?.GetValue<string>();
}
