using System.Text.Json;

namespace RnRomManager.Core.Dats;

/// <summary>Reads and writes <c>dats.json</c>: a JSON array of <see cref="DatManifestEntry"/>.</summary>
public static class DatManifest
{
    public const string FileName = "dats.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <exception cref="JsonException">Not a valid manifest.</exception>
    public static IReadOnlyList<DatManifestEntry> Read(string path) =>
        JsonSerializer.Deserialize<List<DatManifestEntry>>(File.ReadAllBytes(path), Json)
        ?? throw new JsonException($"{path} is empty.");

    public static void Write(string path, IEnumerable<DatManifestEntry> entries) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(entries.ToList(), Json));
}
