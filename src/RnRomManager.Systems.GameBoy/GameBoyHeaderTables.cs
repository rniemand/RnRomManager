using System.Globalization;
using System.Text.Json;

namespace RnRomManager.Systems.GameBoy;

/// <summary>Pan Docs lookup tables (spec §8.1, §8.3), loaded once from the embedded <c>header-tables.json</c>.</summary>
public static class GameBoyHeaderTables
{
    private static readonly Lazy<Tables> Loaded = new(Load);

    public static IReadOnlyDictionary<byte, string> CartridgeTypes => Loaded.Value.CartridgeTypes;

    public static IReadOnlyDictionary<byte, string> OldLicensees => Loaded.Value.OldLicensees;

    /// <summary>Keyed by the two ASCII characters at 0x0144.</summary>
    public static IReadOnlyDictionary<string, string> NewLicensees => Loaded.Value.NewLicensees;

    private sealed record Tables(
        IReadOnlyDictionary<byte, string> CartridgeTypes,
        IReadOnlyDictionary<byte, string> OldLicensees,
        IReadOnlyDictionary<string, string> NewLicensees);

    private sealed record Document(
        Dictionary<string, string> CartridgeTypes,
        Dictionary<string, string> OldLicensees,
        Dictionary<string, string> NewLicensees);

    private static Tables Load()
    {
        var doc = JsonSerializer.Deserialize<Document>(EmbeddedResources.Load("header-tables.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Embedded header-tables.json is empty.");
        return new Tables(ByByte(doc.CartridgeTypes), ByByte(doc.OldLicensees), doc.NewLicensees);
    }

    private static Dictionary<byte, string> ByByte(Dictionary<string, string> table) =>
        table.ToDictionary(e => byte.Parse(e.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture), e => e.Value);
}
