using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

public class SupportedSystemsTableTests
{
    [Test]
    public void ReadmeTable_MatchesTheRegisteredHandlers()
    {
        var services = new ServiceCollection();
        AppHost.AddSystemHandlers(services);
        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetServices<ISystemHandler>().OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase);
        string[] rows =
        [
            "<!-- systems:start -->",
            "| System | Id | ROMs stored | Formats | DAT | Play in browser |",
            "|---|---|---|---|---|---|",
            .. handlers.Select(Row),
            "<!-- systems:end -->",
        ];
        var expected = string.Join('\n', rows);

        var readme = File.ReadAllText(Path.Combine(RepoPaths.Root(), "README.md")).ReplaceLineEndings("\n");
        var section = readme.IndexOf("## Supported systems\n", StringComparison.Ordinal);
        var next = readme.IndexOf("## Quick start\n", StringComparison.Ordinal);
        Assert.That(section, Is.GreaterThanOrEqualTo(0), $"Expected table:\n{expected}");
        Assert.That(next, Is.GreaterThan(section), $"Expected table:\n{expected}");
        Assert.That(readme[(section + "## Supported systems\n".Length)..next].Trim('\n'),
            Is.EqualTo(expected), $"Expected table:\n{expected}");
    }

    private static string Row(ISystemHandler handler)
    {
        var storage = (handler.DefaultEmbedRomsInZip ? "In ZIP" : "Next to ZIP") + (handler.RomStorageConfigurable ? " (configurable)" : "");
        var formats = string.Join(", ", handler.RomExtensions.Select(extension => $"`{extension}`"));
        var dat = handler.DatSource == DatSource.None ? "None" : handler.DatSource == DatSource.TitleDb ? "titledb" : handler.DatSource == DatSource.Mame ? "MAME XML" : handler.DatSource == DatSource.Fbneo ? "FBNeo XML" : handler.DatFolder == DatKind.Redump ? "Redump" : handler.DatFolder == DatKind.Tosec ? "TOSEC" : "No-Intro";
        var core = handler.BrowserCore is { } browserCore ? $"`{browserCore}`" : "—";
        return $"| {handler.DisplayName} | `{handler.Id}` | {storage} | {formats} | {dat} | {core} |";
    }

}
