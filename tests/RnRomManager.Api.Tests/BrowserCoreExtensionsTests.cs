using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Systems;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace RnRomManager.Api.Tests;

/// <summary>
/// Every file type a system ingests must be one its browser core opens. EmulatorJS 4.2.3 hands a single file to the
/// core whatever its extension, so the list that counts is the <c>valid_extensions</c> compiled into the core, not the
/// package's <c>core.json</c> (several of those are wrong: smsplus's is Genesis Plus GX's, handy's lacks <c>lyx</c>).
/// </summary>
public class BrowserCoreExtensionsTests
{
    /// <summary>The EmulatorJS version every list below was read from.</summary>
    private const string CoreVersion = "4.2.3";

    /// <summary>
    /// Each core's <c>valid_extensions</c>, exactly as the NUL-terminated string in its <c>.wasm</c> (from
    /// <c>&lt;core&gt;-wasm.data</c>, PPSSPP's <c>-thread-wasm.data</c>). Vendored cores are re-read from
    /// <c>node_modules</c> by <see cref="VendoredCoreLists_MatchTheirBinaries"/>; operator cores are tied to their payload's
    /// SHA-256 by <see cref="OperatorCoreLists_WereReadFromThePinnedPayloads"/>.
    /// </summary>
    private static readonly Dictionary<string, string> CoreExtensions = new(StringComparer.Ordinal)
    {
        // Vendored (package.json).
        ["a5200"] = "a52|bin",
        ["beetle_vb"] = "vb|vboy|bin",
        ["desmume"] = "nds|ids|bin",
        ["fceumm"] = "fds|nes|unf|unif",
        ["gambatte"] = "gb|gbc|dmg",
        ["gearcoleco"] = "col|cv|bin|rom",
        ["handy"] = "lnx|lyx|o",
        ["mednafen_pce"] = "pce|sgx|cue|ccd|chd|toc|m3u",
        ["mgba"] = "gba|gb|gbc|sgb",
        ["pcsx_rearmed"] = "bin|cue|img|mdf|pbp|toc|cbn|m3u|chd|iso|exe",
        ["ppsspp"] = "elf|iso|cso|prx|pbp|chd",
        ["prosystem"] = "a78|bin|cdf",
        ["puae"] = "adf|adz|dms|fdi|raw|ipf|hdf|hdz|lha|slave|info|cue|ccd|nrg|mds|iso|chd|uae|m3u|zip|7z",
        ["smsplus"] = "sms|bin|rom|col|gg|sg",
        ["stella2014"] = "a26|bin",
        ["virtualjaguar"] = "j64|jag",
        ["yabause"] = "cue|iso|mds|ccd|zip|chd|m3u",
        // Operator-supplied (OperatorCoreManifest).
        ["genesis_plus_gx"] = "m3u|mdx|md|smd|gen|bin|cue|iso|chd|bms|sms|gg|sg|68k|sgd",
        ["picodrive"] = "bin|gen|smd|md|32x|cue|iso|chd|sms|gg|sg|sc|m3u|68k|sgd|pco",
        ["snes9x"] = "smc|sfc|swc|fig|bs|st",
        ["opera"] = "iso|bin|chd|cue",
        ["vice_x64sc"] = "d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|tcrt",
        ["vice_xpet"] = "d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|tcrt",
        ["vice_xvic"] = "d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|20|40|60|a0|b0|rom",
        ["mupen64plus_next"] = "n64|v64|z64|bin|u1",
    };

    /// <summary>The SHA-256 of the <c>&lt;core&gt;-wasm.data</c> each operator core's list above was read from.</summary>
    private static readonly Dictionary<string, string> OperatorPayloadSha256 = new(StringComparer.Ordinal)
    {
        ["genesis_plus_gx"] = "190297a6f86757405090f1a2266f67dfe1a570a528c583434ed3641a5664f768",
        ["picodrive"] = "bb5d50b8b88111b583977d2f7a16d01a822b3deda9205048d99ecaad2c56d861",
        ["snes9x"] = "eaa0bcfce67673809886e50387a80a616b719502175db64c090d04c9d75958ee",
        ["opera"] = "3e737f4f739814c12c017a5f26cb1e43bdfe3ac6f2d3bf6e8972633df49e33d4",
        ["vice_x64sc"] = "77f58884c81b58721cbc4754ffc5574838219258e9390630c352a494a3f335ab",
        ["vice_xpet"] = "eef0d83f7c1af43d51c3384a07db4196ea7c2bbe1292e9e52fade3d3107513c5",
        ["vice_xvic"] = "4c7de6f858e100e9aea93085d5aef01bdfe87399c21c0fde4e1b6c7f1da56316",
        ["mupen64plus_next"] = "2da1cbce9fda395e3ae83ca5787353baa159142d45ef3ea90f108b92524f76cc",
    };

    /// <summary>
    /// A system's extension its core doesn't list, and why it's still right. An entry that no longer applies (the
    /// handler dropped the extension, the core now lists it, or the system has another core) fails the test.
    /// </summary>
    private static readonly Dictionary<(string System, string Extension), string> Exceptions = new()
    {
        [("saturn", ".bin")] = "play-disc adds a MODE1/2352 cue to a lone .bin, and Yabause opens the cue",
        [("saturn", ".nrg")] = "never plays: canPlay and the games list's playRomId refuse an .nrg release",
        [("tgcd", ".bin")] = "a cue set's data track; a lone .bin never plays (canPlay, play-disc)",
        [("jaguar", ".rom")] = "VirtualJaguar picks the format from the file's content (unverified with a real dump)",
        [("jaguar", ".abs")] = "VirtualJaguar picks the format from the file's content (unverified with a real dump)",
        [("jaguar", ".cof")] = "VirtualJaguar picks the format from the file's content (unverified with a real dump)",
    };

    [Test]
    public void EveryHandlerExtension_IsOpenedByItsBrowserCore()
    {
        Assert.That(Problems(Handlers()), Is.Empty);
    }

    [Test]
    public void ProblemsAreNamed()
    {
        var handlers = new[]
        {
            Handler("gb", "gambatte", ".gb", ".zzz"),
            Handler("x", "nocore", ".x"),
            Handler("saturn", "yabause", ".iso"),
        };
        Assert.That(Problems(handlers, ["gambatte", "yabause", "stray"]), Is.EquivalentTo(new[]
        {
            "gb: gambatte doesn't open .zzz (gb|gbc|dmg)",
            "x: nocore has no extension list",
            "stray: no system uses this core",
            "saturn .bin: stale exception (not one of the system's extensions)",
            "saturn .nrg: stale exception (not one of the system's extensions)",
            "jaguar .rom: stale exception (no such system)",
            "jaguar .abs: stale exception (no such system)",
            "jaguar .cof: stale exception (no such system)",
        }));
    }

    [Test]
    public void OperatorCoreLists_WereReadFromThePinnedPayloads()
    {
        var problems = new List<string>();
        foreach (var core in OperatorCoreManifest.Pinned.Cores)
        {
            var payload = core.Find($"{core.Id}-wasm.data");
            if (!OperatorPayloadSha256.TryGetValue(core.Id, out var sha))
                problems.Add($"{core.Id}: no list recorded; read its valid_extensions from the payload");
            else if (payload is null || payload.Sha256 != sha)
                problems.Add($"{core.Id}: the pinned payload changed; re-read its valid_extensions and update both tables");
        }
        foreach (var id in OperatorPayloadSha256.Keys.Except(OperatorCoreManifest.Pinned.Cores.Select(c => c.Id)))
            problems.Add($"{id}: not an operator core");
        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void VendoredCores_AreTheVersionTheListsWereReadFrom()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(WebRoot(), "package.json")));
        var vendored = VendoredCores(package.RootElement).ToList();
        var operatorIds = OperatorCoreManifest.Pinned.Cores.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (core, version) in vendored)
        {
            if (!CoreExtensions.ContainsKey(core)) problems.Add($"{core}: no list recorded");
            if (version != CoreVersion) problems.Add($"{core}: {version} is vendored but the list is from {CoreVersion}; re-read it");
        }
        foreach (var core in CoreExtensions.Keys.Where(c => !operatorIds.Contains(c)).Except(vendored.Select(v => v.Core)))
            problems.Add($"{core}: neither vendored nor an operator core");
        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void VendoredCoreLists_MatchTheirBinaries()
    {
        var modules = Path.Combine(WebRoot(), "node_modules", "@emulatorjs");
        if (!Directory.Exists(modules))
            Assert.Ignore("Run `npm ci` in src/RnRomManager.Web to check the vendored cores' binaries.");

        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(WebRoot(), "package.json")));
        var problems = new List<string>();
        foreach (var (core, _) in VendoredCores(package.RootElement))
        {
            var folder = Path.Combine(modules, $"core-{core}");
            var payload = new[] { $"{core}-wasm.data", $"{core}-thread-wasm.data" }
                .Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
            if (payload is null) { problems.Add($"{core}: no payload in {folder}"); continue; }
            var wasm = ReadWasm(payload);
            if (wasm is null) { problems.Add($"{core}: no .wasm in {Path.GetFileName(payload)}"); continue; }
            if (CoreExtensions.TryGetValue(core, out var list) && !ContainsCString(wasm, list))
                problems.Add($"{core}: its binary doesn't hold \"{list}\"");
        }
        Assert.That(problems, Is.Empty);
    }

    private static IEnumerable<string> Problems(IEnumerable<ISystemHandler> handlers, IEnumerable<string>? cores = null)
    {
        var list = handlers.ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handler in list)
        {
            if (handler.BrowserCore is not { } core) continue;
            used.Add(core);
            if (!CoreExtensions.TryGetValue(core, out var extensions)) { yield return $"{handler.Id}: {core} has no extension list"; continue; }
            var opened = extensions.Split('|').Select(e => "." + e).ToHashSet(StringComparer.Ordinal);
            foreach (var extension in handler.RomExtensions)
                if (!opened.Contains(extension) && !Exceptions.ContainsKey((handler.Id, extension)))
                    yield return $"{handler.Id}: {core} doesn't open {extension} ({extensions})";
        }
        foreach (var core in (cores ?? CoreExtensions.Keys).Except(used, StringComparer.Ordinal))
            yield return $"{core}: no system uses this core";

        foreach (var ((system, extension), _) in Exceptions)
        {
            var handler = list.FirstOrDefault(h => h.Id == system);
            if (handler is null) yield return $"{system} {extension}: stale exception (no such system)";
            else if (!handler.RomExtensions.Contains(extension)) yield return $"{system} {extension}: stale exception (not one of the system's extensions)";
            else if (handler.BrowserCore is not { } core || !CoreExtensions.TryGetValue(core, out var extensions))
                yield return $"{system} {extension}: stale exception (no browser core)";
            else if (extensions.Split('|').Contains(extension[1..]))
                yield return $"{system} {extension}: stale exception ({core} now opens it)";
        }
    }

    private static IEnumerable<(string Core, string Version)> VendoredCores(JsonElement package) =>
        package.GetProperty("dependencies").EnumerateObject()
            .Where(p => p.Name.StartsWith("@emulatorjs/core-", StringComparison.Ordinal))
            .Select(p => (p.Name["@emulatorjs/core-".Length..], p.Value.GetString()!));

    private static byte[]? ReadWasm(string payload)
    {
        using var stream = File.OpenRead(payload);
        using var archive = SevenZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory || reader.Entry.Key?.EndsWith(".wasm", StringComparison.Ordinal) != true) continue;
            using var entry = reader.OpenEntryStream();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
        return null;
    }

    private static bool ContainsCString(byte[] haystack, string value)
    {
        var needle = new byte[value.Length + 2];
        Encoding.ASCII.GetBytes(value, 0, value.Length, needle, 1);
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static List<ISystemHandler> Handlers()
    {
        var services = new ServiceCollection();
        AppHost.AddSystemHandlers(services);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<ISystemHandler>().ToList();
    }

    private static ISystemHandler Handler(string id, string core, params string[] extensions)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns(id);
        handler.BrowserCore.Returns(core);
        handler.RomExtensions.Returns(extensions);
        return handler;
    }

    private static string WebRoot() => Path.Combine(RepoRoot(), "src", "RnRomManager.Web");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RnRomManager.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
