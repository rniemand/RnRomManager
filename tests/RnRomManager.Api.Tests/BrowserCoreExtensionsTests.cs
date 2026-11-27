using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
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
        ["dosbox_pure"] = "zip|dosz|exe|com|bat|iso|chd|cue|ins|img|ima|vhd|jrc|tc|m3u|m3u8|conf|/",
        ["fceumm"] = "fds|nes|unf|unif",
        ["gambatte"] = "gb|gbc|dmg",
        ["gearcoleco"] = "col|cv|bin|rom",
        ["handy"] = "lnx|lyx|o",
        ["mednafen_ngp"] = "ngp|ngc|ngpc|npc",
        ["mednafen_pce"] = "pce|sgx|cue|ccd|chd|toc|m3u",
        ["mednafen_pcfx"] = "cue|ccd|toc|chd",
        ["mednafen_wswan"] = "ws|wsc|pc2",
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
        ["vice_x128"] = "d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|tcrt",
        ["vice_xplus4"] = "d64|d71|d80|d81|d82|g64|g41|x64|t64|tap|prg|p00|crt|bin|zip|7z|gz|d6z|d7z|d8z|g6z|g4z|x6z|cmd|m3u|vfl|vsf|nib|nbz|d2m|d4m|tcrt",
        ["mupen64plus_next"] = "n64|v64|z64|bin|u1",
        // Arcade P1: the NUL-terminated valid_extensions string in mame2003_plus_libretro.wasm is "zip|" (read 2026-11-24), its
        // trailing separator dropped here; core.json says ["zip"].
        ["mame2003_plus"] = "zip",
        // Arcade (FinalBurn Neo) P1: the NUL-terminated valid_extensions string in fbneo_libretro.wasm is "zip|7z|cue|ccd"
        // (read 2026-11-24; the cue/ccd are Neo Geo CD).
        ["fbneo"] = "zip|7z|cue|ccd",
        // Side by side (emulatorjs-next.json, EmulatorJS 4.3.0-pre): tied to its payload by NextCoreSha256.
        ["azahar"] = "3ds|3dsx|z3dsx|elf|axf|cci|zcci|cxi|zcxi|app",
    };

    /// <summary>3DS: the <c>emulatorjs-next.json</c> core and the SHA-256 of the payload its list above was read from.</summary>
    private const string NextCore = "azahar";
    private const string NextCoreSha256 = "d90696e6ea68c4fc00ef147411ad399962777f07b6c7e73d5537da0eaffc2e3b";

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
        ["vice_x128"] = "dbac85e530b006c2d17e200c445de582c7ea272bacf333e4b4f8c1e391ed8506",
        ["vice_xplus4"] = "f96b6660211a403b687e5f905394fda655c60250db7619a335ef2ccc160bfb30",
        ["mupen64plus_next"] = "2da1cbce9fda395e3ae83ca5787353baa159142d45ef3ea90f108b92524f76cc",
        ["mame2003_plus"] = "cb6d9c80a88b65d1579d16d02128a678f8d1cd3f51de1479e647cea27b13247b",
        ["fbneo"] = "315a25e0bcd61d58ee0d9e8b1dbf3740b9e0ca4b7d0726f848ce1068de73437c",
    };

    /// <summary>
    /// A system's extension its core doesn't list, and why it's still right. An entry that no longer applies (the
    /// handler dropped the extension, the core now lists it, or the system has another core) fails the test.
    /// </summary>
    private const string CloneCdMember = "CloneCD set member: play-disc sends a generated cue (B8), never this file as the content";
    private const string MdsTocMember = "MDS or TOC set member: play-disc sends a generated cue (D12), never this file as the content";

    private static readonly Dictionary<(string System, string Extension), string> Exceptions = new()
    {
        [("mame2003", ".mamez")] = "stored as .mamez and handed to the core as <set>.zip by play.js",
        [("fbneo", ".fbneoz")] = "stored as .fbneoz and handed to the core as <set>.zip by play.js",
        [("fbneo", ".fbneo7z")] = "stored as .fbneo7z and handed to the core as <set>.7z by play.js",
        [("saturn", ".bin")] = "play-disc adds a MODE1/2352 cue to a lone .bin, and Yabause opens the cue",
        [("saturn", ".nrg")] = "play-disc sends a cue built from the Nero footer and the image as track-01.bin (N1), never the .nrg as the content",
        [("tgcd", ".bin")] = "a cue set's data track; a lone .bin never plays (canPlay, play-disc)",
        [("pcfx", ".bin")] = "a cue set's data track; a lone .bin never plays (canPlay, play-disc)",
        [("lynx", ".bll")] = "Handy loads BLL homebrew by its BS93 header (checked 2026-11-23 with the DAT's Blasting Robots .bll, CRC 06428D44)",
        [("gb", ".sgb")] = "an SGB-enhanced Game Boy ROM: stored as the DAT's .gb or, through Review, as .gb ([0]); never stored as .sgb",
        [("sms", ".bms")] = "a TecToy Brazilian Master System ROM: stored as the DAT's .sms or, through Review, as .sms ([0]); never stored as .bms",
        [("jaguar", ".rom")] = "VirtualJaguar sizes a header-less .rom as an Alpine image (checked 2026-11-23: SkyFire68k's .j64 minus its 8 KiB header boots); canPlay and playRomId refuse other sizes",
        [("jaguar", ".abs")] = "library only: VirtualJaguar loads an ABS into RAM, which this build's reset wipes (checked 2026-11-23 with the DAT's Blackhole .abs); canPlay and playRomId refuse it",
        [("jaguar", ".cof")] = "library only: VirtualJaguar loads a COFF into RAM, which this build's reset wipes; canPlay and playRomId refuse it",
        [("psx", ".ccd")] = CloneCdMember,
        [("psx", ".sub")] = CloneCdMember,
        [("saturn", ".img")] = CloneCdMember,
        [("saturn", ".sub")] = CloneCdMember,
        [("segacd", ".ccd")] = CloneCdMember,
        [("segacd", ".img")] = CloneCdMember,
        [("segacd", ".sub")] = CloneCdMember,
        [("3do", ".ccd")] = CloneCdMember,
        [("3do", ".img")] = CloneCdMember,
        [("3do", ".sub")] = CloneCdMember,
        [("tgcd", ".img")] = CloneCdMember,
        [("tgcd", ".sub")] = CloneCdMember,
        [("pcfx", ".img")] = CloneCdMember,
        [("pcfx", ".sub")] = CloneCdMember,
        [("psx", ".mds")] = MdsTocMember,
        [("saturn", ".mdf")] = MdsTocMember,
        [("saturn", ".toc")] = MdsTocMember,
        [("segacd", ".mds")] = MdsTocMember,
        [("segacd", ".mdf")] = MdsTocMember,
        [("segacd", ".toc")] = MdsTocMember,
        [("3do", ".mds")] = MdsTocMember,
        [("3do", ".mdf")] = MdsTocMember,
        [("3do", ".toc")] = MdsTocMember,
        [("tgcd", ".mds")] = MdsTocMember,
        [("tgcd", ".mdf")] = MdsTocMember,
        [("pcfx", ".mds")] = MdsTocMember,
        [("pcfx", ".mdf")] = MdsTocMember,
        [("3ds", ".cia")] = "library only: azahar's libretro build can't load a CIA; canPlay and the quick play refuse it",
    };

    private const string Archive = "ingest unpacks .zip and .7z; the core never gets one";
    private const string Playlist = "the play page builds its own .m3u for a disc or disk set";
    private const string OtherSystem = "another system's type, ingested on that system's core";
    private const string NewSystem = "would be a new system";
    private const string NotAGame = "not a game image (a snapshot, config, command, fliplist or icon file)";
    private const string NoDat = "homebrew or unusual type no DAT lists; left out by the 2026-11-23 audit";
    private const string OtherCommodore = "ingested only on another Commodore system here (the PET has no cartridge port; NIB, D2M/D4M and 8050/8250 images aren't taken for this system)";
    private const string C128Scope = "left out of Commodore 128 by its spec (Out of scope); ingested on C64";
    private const string Plus4Scope = "left out of Commodore Plus/4 by its spec (Out of scope)";
    private static readonly string[] Vice = ["vice_x64sc", "vice_xpet", "vice_xvic", "vice_x128", "vice_xplus4"];

    /// <summary>
    /// P2: a type a core opens that no system on that core ingests, and why. An entry the core no longer lists, or that a
    /// handler on that core now ingests, fails the test.
    /// </summary>
    private static readonly Dictionary<(string Core, string Extension), string> NotIngested = Entries(
        (Archive, ["puae", .. Vice], [".zip", ".7z"]),
        (Archive, ["yabause"], [".zip"]),
        (Playlist, ["mednafen_pce", "pcsx_rearmed", "yabause", "puae", "genesis_plus_gx", "picodrive", .. Vice], [".m3u"]),
        (OtherSystem, ["mgba"], [".gb", ".gbc", ".sgb"]),
        (OtherSystem, ["genesis_plus_gx"], [".sms", ".gg", ".sg", ".bms"]),
        (OtherSystem, ["picodrive"], [".gen", ".smd", ".md", ".cue", ".iso", ".chd", ".sms", ".gg", ".sg", ".68k", ".sgd"]),
        (OtherSystem, ["smsplus"], [".col"]),
        (NewSystem, ["picodrive"], [".sc", ".pco"]),
        (NewSystem, ["puae"], [".cue", ".ccd", ".nrg", ".mds", ".iso", ".chd"]),
        (NotAGame, Vice, [".vsf", ".cmd", ".vfl"]),
        (NotAGame, ["puae"], [".uae", ".info", ".slave"]),
        (NoDat, ["puae"], [".hdf", ".hdz", ".lha", ".fdi", ".raw", ".dms"]),
        ("a ProSystem cartridge description: a text file naming a separate ROM (and BupChip sample) file, not a ROM; a release is one file", ["prosystem"], [".cdf"]),
        (NoDat, Vice, [".g41", ".g4z"]),
        ("the DS DAT's .bin entries are save data and seminar demos, which DatStore would match (D1)", ["desmume"], [".bin"]),
        (OtherCommodore, ["vice_xpet"], [".crt", ".bin", ".tcrt", ".nib", ".nbz", ".d2m", ".d4m"]),
        (OtherCommodore, ["vice_xvic"], [".nib", ".nbz", ".d2m", ".d4m", ".d80", ".d82"]),
        (C128Scope, ["vice_x128"], [".bin", ".x64", ".d80", ".d82", ".nib", ".nbz", ".d2m", ".d4m", ".tcrt", ".gz", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z"]),
        (Plus4Scope, ["vice_xplus4"], [".d71", ".d80", ".d81", ".d82", ".g64", ".x64", ".p00", ".nib", ".nbz", ".d2m", ".d4m", ".tcrt", ".gz", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z"]),
        ("DOS games are ingested as one .zip or .dosz; put loose files and disc images inside it", ["dosbox_pure"],
            [".exe", ".com", ".bat", ".iso", ".chd", ".cue", ".ins", ".img", ".ima", ".vhd", ".jrc", ".tc", ".m3u", ".m3u8", ".conf", "./"]),
        ("3DS homebrew and NCCH formats aren't ingested; only .3ds, .cci and .cia dumps are", ["azahar"],
            [".3dsx", ".z3dsx", ".elf", ".axf", ".zcci", ".cxi", ".zcxi", ".app"]),
        ("Neo Geo CD and other non-arcade content isn't an arcade set; not ingested", ["fbneo"], [".cue", ".ccd"]));

    private static Dictionary<(string Core, string Extension), string> Entries(params (string Reason, string[] Cores, string[] Extensions)[] groups)
    {
        var entries = new Dictionary<(string Core, string Extension), string>();
        foreach (var (reason, cores, extensions) in groups)
            foreach (var core in cores)
                foreach (var extension in extensions)
                    entries.Add((core, extension), reason); // Add: a repeated entry throws
        return entries;
    }

    [Test]
    public void EveryCoreExtension_IsIngestedOrListed()
    {
        Assert.That(UningestedProblems(Handlers()), Is.Empty);
    }

    [Test]
    public void UningestedProblemsAreNamed()
    {
        var cores = new Dictionary<string, string> { ["gambatte"] = "gb|gbc|dmg", ["yabause"] = "cue|zip" };
        var handlers = new[] { Handler("gb", "gambatte", ".gb", ".gbc"), Handler("saturn", "yabause", ".cue") };
        var notIngested = new Dictionary<(string Core, string Extension), string> { [("gambatte", ".gbc")] = "x", [("yabause", ".iso")] = "x" };

        Assert.That(UningestedProblems(handlers, cores, notIngested), Is.EquivalentTo(new[]
        {
            "gambatte: no system ingests .dmg",
            "gambatte .gbc: stale entry (gb ingests it)",
            "yabause: no system ingests .zip",
            "yabause .iso: stale entry (yabause doesn't list it)",
        }));
    }

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
            "lynx .bll: stale exception (no such system)",
            "gb .sgb: stale exception (not one of the system's extensions)",
            "sms .bms: stale exception (no such system)",
            "jaguar .rom: stale exception (no such system)",
            "jaguar .abs: stale exception (no such system)",
            "jaguar .cof: stale exception (no such system)",
            "tgcd .bin: stale exception (no such system)",
            "pcfx .bin: stale exception (no such system)",
            "psx .ccd: stale exception (no such system)",
            "psx .sub: stale exception (no such system)",
            "saturn .img: stale exception (not one of the system's extensions)",
            "saturn .sub: stale exception (not one of the system's extensions)",
            "segacd .ccd: stale exception (no such system)",
            "segacd .img: stale exception (no such system)",
            "segacd .sub: stale exception (no such system)",
            "3do .ccd: stale exception (no such system)",
            "3do .img: stale exception (no such system)",
            "3do .sub: stale exception (no such system)",
            "tgcd .img: stale exception (no such system)",
            "tgcd .sub: stale exception (no such system)",
            "pcfx .img: stale exception (no such system)",
            "pcfx .sub: stale exception (no such system)",
            "psx .mds: stale exception (no such system)",
            "saturn .mdf: stale exception (not one of the system's extensions)",
            "saturn .toc: stale exception (not one of the system's extensions)",
            "segacd .mds: stale exception (no such system)",
            "segacd .mdf: stale exception (no such system)",
            "segacd .toc: stale exception (no such system)",
            "3do .mds: stale exception (no such system)",
            "3do .mdf: stale exception (no such system)",
            "3do .toc: stale exception (no such system)",
            "tgcd .mds: stale exception (no such system)",
            "tgcd .mdf: stale exception (no such system)",
            "pcfx .mds: stale exception (no such system)",
            "pcfx .mdf: stale exception (no such system)",
            "3ds .cia: stale exception (no such system)",
            "mame2003 .mamez: stale exception (no such system)",
            "fbneo .fbneoz: stale exception (no such system)",
            "fbneo .fbneo7z: stale exception (no such system)",
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
        // 3DS: azahar comes from EmulatorJS 4.3.0-pre (emulatorjs-next.json), not npm; its list is tied to the pinned payload.
        using var next = JsonDocument.Parse(File.ReadAllText(Path.Combine(WebRoot(), "emulatorjs-next.json")));
        if (next.RootElement.GetProperty("core").GetProperty("sha256").GetString() != NextCoreSha256)
            problems.Add($"{NextCore}: the pinned payload changed; re-read its valid_extensions and update NextCoreSha256");
        foreach (var core in CoreExtensions.Keys.Where(c => !operatorIds.Contains(c) && c != NextCore).Except(vendored.Select(v => v.Core)))
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

    /// <summary>P1: every type a core opens is ingested by a system on that core, or listed in <paramref name="notIngested"/>.</summary>
    private static IEnumerable<string> UningestedProblems(IEnumerable<ISystemHandler> handlers, IReadOnlyDictionary<string, string>? cores = null,
        IReadOnlyDictionary<(string Core, string Extension), string>? notIngested = null)
    {
        cores ??= CoreExtensions;
        notIngested ??= NotIngested;
        var list = handlers.ToList();
        string? IngestedBy(string core, string extension) =>
            list.FirstOrDefault(h => h.BrowserCore == core && h.RomExtensions.Contains(extension))?.Id;

        foreach (var (core, extensions) in cores)
            foreach (var extension in extensions.Split('|').Select(e => "." + e))
                if (IngestedBy(core, extension) is null && !notIngested.ContainsKey((core, extension)))
                    yield return $"{core}: no system ingests {extension}";
        foreach (var ((core, extension), _) in notIngested)
        {
            if (!cores.TryGetValue(core, out var extensions) || !extensions.Split('|').Contains(extension[1..]))
                yield return $"{core} {extension}: stale entry ({core} doesn't list it)";
            else if (IngestedBy(core, extension) is { } system)
                yield return $"{core} {extension}: stale entry ({system} ingests it)";
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
        var handler = TestHandlers.Fake(id);
        handler.BrowserCore.Returns(core);
        handler.RomExtensions.Returns(extensions);
        return handler;
    }

    private static string WebRoot() => Path.Combine(RepoPaths.Root(), "src", "RnRomManager.Web");

}
