using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>
/// Play core parity C1–C5: the browser-play core lists written in both the API and the SPA (<c>emulator.ts</c>,
/// <c>params.js</c>) stay in step, so the game page and the API agree on what plays.
/// </summary>
public sealed class PlayCoreParityTests
{
    [Test]
    public void SpaAndApiCoreLists_Match()
    {
        var web = Path.Combine(RepoPaths.Root(), "src", "RnRomManager.Web");
        var emulatorTs = File.ReadAllText(Path.Combine(web, "src", "lib", "emulator.ts"));
        var paramsJs = File.ReadAllText(Path.Combine(web, "static", "emulator", "params.js"));

        Assert.That(Problems(emulatorTs, paramsJs, LibraryEndpoints.ExternalBrowserCores, LibraryEndpoints.PlayDiscCores,
            PlayDisc.SupportedSystems, Handlers()), Is.Empty);
    }

    [Test]
    public void ProblemsAreNamed()
    {
        const string emulatorTs = """
            export const externalBrowserCores: readonly string[] = ['a', "b", 'b', 'x'];
            export const playDiscCores: readonly string[] = [
            	"c",
            	'e'
            ];
            """;
        const string paramsJs = "const cores = [\n\t'a',\n\t\"z\"\n];";
        var handlers = new[]
        {
            Handler("one", "a"),
            Handler("two", "c"),
            Handler("three", "c", embedRomsInZip: true),
            Handler("four", null),
            Handler("five", "d"),
        };

        Assert.That(Problems(emulatorTs, paramsJs, Set("a", "b", "c"), Set("c", "d"), Set("two", "nine"), handlers),
            Is.EquivalentTo(new[]
            {
                "emulator.ts externalBrowserCores: b listed twice",
                "emulator.ts externalBrowserCores: missing c",
                "emulator.ts externalBrowserCores: x is not in LibraryEndpoints.ExternalBrowserCores",
                "emulator.ts playDiscCores: missing d",
                "emulator.ts playDiscCores: e is not in LibraryEndpoints.PlayDiscCores",
                "PlayDiscCores: d is not in ExternalBrowserCores",
                "params.js cores: missing c (used by two)",
                "params.js cores: missing d (used by five)",
                "params.js cores: z is no system's BrowserCore",
                "PlayDisc.SupportedSystems: missing five",
                "PlayDisc.SupportedSystems: nine is not an external system on a play-disc core",
            }));
    }

    [Test]
    public void MissingDeclarations_AreNamed_AndSkipTheirComparisons()
    {
        Assert.That(Problems("const externalBrowserCores = ['a'];", "let cores = ['a'];", Set("a"), Set(), Set(), [Handler("one", "a")]),
            Is.EquivalentTo(new[]
            {
                "emulator.ts: externalBrowserCores not found",
                "emulator.ts: playDiscCores not found",
                "params.js: cores not found",
            }));
    }

    private static List<string> Problems(
        string emulatorTs,
        string paramsJs,
        IReadOnlySet<string> externalBrowserCores,
        IReadOnlySet<string> playDiscCores,
        IReadOnlySet<string> playDiscSystems,
        IReadOnlyList<ISystemHandler> handlers)
    {
        var problems = new List<string>();

        // C1, C2
        foreach (var (name, apiName, api) in new[] { ("externalBrowserCores", "ExternalBrowserCores", externalBrowserCores), ("playDiscCores", "PlayDiscCores", playDiscCores) })
        {
            if (Extract(emulatorTs, "emulator.ts", name, $@"export const {name}: readonly string\[\] = \[(.*?)\];", problems) is not { } spa) continue;
            problems.AddRange(api.Except(spa).Select(core => $"emulator.ts {name}: missing {core}"));
            problems.AddRange(spa.Except(api).Select(core => $"emulator.ts {name}: {core} is not in LibraryEndpoints.{apiName}"));
        }

        // C3
        problems.AddRange(playDiscCores.Except(externalBrowserCores).Select(core => $"PlayDiscCores: {core} is not in ExternalBrowserCores"));

        // C4: GroupBy keeps registration order, so First() is the first system on the core.
        var firstUser = handlers.Where(h => h.BrowserCore is not null).GroupBy(h => h.BrowserCore ?? "").ToDictionary(g => g.Key, g => g.First().Id);
        if (Extract(paramsJs, "params.js", "cores", @"\bconst cores = \[(.*?)\];", problems) is { } cores)
        {
            problems.AddRange(firstUser.Keys.Except(cores).Select(core => $"params.js cores: missing {core} (used by {firstUser[core]})"));
            problems.AddRange(cores.Except(firstUser.Keys).Select(core => $"params.js cores: {core} is no system's BrowserCore"));
        }

        // C5
        var expected = handlers.Where(h => h.BrowserCore is { } core && playDiscCores.Contains(core) && !h.EmbedRomsInZip).Select(h => h.Id).ToList();
        problems.AddRange(expected.Except(playDiscSystems).Select(id => $"PlayDisc.SupportedSystems: missing {id}"));
        problems.AddRange(playDiscSystems.Except(expected).Select(id => $"PlayDisc.SupportedSystems: {id} is not an external system on a play-disc core"));
        return problems;
    }

    /// <summary>The quoted entries of one array declaration, or null (a "not found" problem) when the declaration is gone.</summary>
    private static List<string>? Extract(
        string text,
        string file,
        string name,
        string declaration,
        List<string> problems)
    {
        var match = Regex.Match(text, declaration, RegexOptions.Singleline);
        if (!match.Success)
        {
            problems.Add($"{file}: {name} not found");
            return null;
        }
        var entries = Regex.Matches(match.Groups[1].Value, @"'([a-z0-9_]+)'|""([a-z0-9_]+)""")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        problems.AddRange(entries.GroupBy(e => e).Where(g => g.Count() > 1).Select(g => $"{file} {name}: {g.Key} listed twice"));
        return entries;
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static ISystemHandler Handler(string id, string? core, bool embedRomsInZip = false)
    {
        var handler = TestHandlers.Fake(id, embedRomsInZip);
        handler.BrowserCore.Returns(core);
        return handler;
    }

    private static List<ISystemHandler> Handlers()
    {
        var services = new ServiceCollection();
        AppHost.AddSystemHandlers(services);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<ISystemHandler>().ToList();
    }
}
