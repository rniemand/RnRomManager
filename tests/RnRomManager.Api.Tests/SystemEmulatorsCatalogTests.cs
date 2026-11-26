using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>Systems tab B9: the SPA's emulator catalog covers exactly the registered systems and is well-formed.</summary>
public class SystemEmulatorsCatalogTests
{
    private static readonly string[] OsValues = ["windows", "macos", "linux", "android", "ios"];

    [Test]
    public void Catalog_MatchesTheRegisteredHandlers_AndFollowsTheRules()
    {
        var services = new ServiceCollection();
        AppHost.AddSystemHandlers(services);
        using var provider = services.BuildServiceProvider();
        var ids = provider.GetServices<ISystemHandler>().Select(h => h.Id).ToList();
        var path = Path.Combine(RepoPaths.Root(), "src", "RnRomManager.Web", "src", "lib", "systemEmulators.json");
        using var catalog = JsonDocument.Parse(File.ReadAllText(path));

        Assert.That(Problems(catalog.RootElement, ids), Is.Empty);
    }

    [Test]
    public void MissingAndStrayKeys_AreNamed()
    {
        Assert.That(Check("""{ "gb": [], "zzz": [] }""", "gb", "gbc"),
            Is.EquivalentTo(new[] { "gbc: missing from the catalog", "zzz: not a registered system" }));
    }

    [TestCase("""{ "gb": {} }""", "gb: must be an array")]
    [TestCase("""{ "gb": [{ "name": "", "url": "https://a.org/", "os": ["windows"] }] }""", "gb: an emulator has no name")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "https://a.org/", "os": ["windows"] }, { "name": "A", "url": "https://a.org/", "os": ["ios"] }] }""", "gb: A is listed twice")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "http://a.org/", "os": ["windows"] }] }""", "gb: A url must be absolute https")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "/a", "os": ["windows"] }] }""", "gb: A url must be absolute https")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "https://a.org/", "os": [] }] }""", "gb: A os must be a non-empty list")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "https://a.org/", "os": ["dos"] }] }""", "gb: A os has unknown value dos")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "https://a.org/", "os": ["ios", "ios"] }] }""", "gb: A os lists ios twice")]
    [TestCase("""{ "gb": [{ "name": "A", "url": "https://a.org/", "os": ["ios"], "note": " " }] }""", "gb: A note is empty")]
    public void BadEntries_AreReported(string json, string problem)
    {
        Assert.That(Check(json, "gb"), Is.EqualTo(new[] { problem }));
    }

    [Test]
    public void SameName_MustHaveTheSameUrlEverywhere()
    {
        Assert.That(Check("""
            { "gb": [{ "name": "A", "url": "https://a.org/", "os": ["ios"] }],
              "gbc": [{ "name": "A", "url": "https://b.org/", "os": ["ios"] }] }
            """, "gb", "gbc"), Is.EqualTo(new[] { "gbc: A url https://b.org/ differs from https://a.org/" }));
    }

    private static List<string> Check(string json, params string[] ids)
    {
        using var document = JsonDocument.Parse(json);
        return Problems(document.RootElement, ids).ToList();
    }

    private static IEnumerable<string> Problems(JsonElement catalog, IReadOnlyCollection<string> ids)
    {
        var keys = catalog.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var id in ids.Except(keys, StringComparer.Ordinal)) yield return $"{id}: missing from the catalog";
        foreach (var key in keys.Except(ids, StringComparer.Ordinal)) yield return $"{key}: not a registered system";

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var system in catalog.EnumerateObject())
        {
            if (system.Value.ValueKind != JsonValueKind.Array) { yield return $"{system.Name}: must be an array"; continue; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var emulator in system.Value.EnumerateArray())
            {
                var name = Text(emulator, "name");
                if (string.IsNullOrWhiteSpace(name)) { yield return $"{system.Name}: an emulator has no name"; continue; }
                if (!names.Add(name)) yield return $"{system.Name}: {name} is listed twice";

                var url = Text(emulator, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    yield return $"{system.Name}: {name} url must be absolute https";
                else if (!urls.TryAdd(name, url!) && urls[name] != url)
                    yield return $"{system.Name}: {name} url {url} differs from {urls[name]}";

                if (!emulator.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.Array || os.GetArrayLength() == 0)
                    yield return $"{system.Name}: {name} os must be a non-empty list";
                else
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in os.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()))
                    {
                        if (!OsValues.Contains(value)) yield return $"{system.Name}: {name} os has unknown value {value}";
                        else if (!seen.Add(value)) yield return $"{system.Name}: {name} os lists {value} twice";
                    }
                }

                if (emulator.TryGetProperty("note", out var note) && (note.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(note.GetString())))
                    yield return $"{system.Name}: {name} note is empty";
            }
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}
