using System.Security.Cryptography;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>GET /api/admin/bios</c> (mapped in <see cref="AdminEndpoints"/>): every BIOS file the play page loads from <c>Paths:Bios</c>, whether it's there and
/// whether its size passes, so an Admin can check a deployment. The sizes mirror the play page's checks
/// (<c>static/emulator/params.js</c>) and <c>docs/configuration.md</c>; keep them in step.
/// </summary>
public static class BiosInfo
{
    private const long KiB = 1024, MiB = 1024 * 1024;
    private static BiosFile Exact(string name, long bytes) => new(name, bytes, bytes);
    private static BiosFile AtLeast(string name, long bytes) => new(name, bytes, MiB * 16);
    private static BiosFile UpTo(string name, long bytes) => new(name, 1, bytes);
    private const string Cd32Group = "Commodore Amiga CD32", Cd32ExtGroup = "Commodore Amiga CD32 (extended ROM)";

    public static readonly BiosInfoGroup[] Catalog =
    [
        new("ColecoVision", Need.All, [Exact("colecovision.rom", 8 * KiB)], "Gearcoleco needs it for every game."),
        new("Famicom Disk System", Need.All, [Exact("disksys.rom", 8 * KiB)], "FCEUmm needs it for .fds releases."),
        new("Satellaview", Need.All, [AtLeast("BS-X.bin", MiB)], "Snes9x needs it for .bs releases.", "Emulator:Snes9xCorePath"),
        new("Sufami Turbo", Need.All, [AtLeast("STBIOS.bin", 256 * KiB)], "Snes9x needs it for .st releases.", "Emulator:Snes9xCorePath"),
        new("PlayStation", Need.Optional, [Exact("scph5501.bin", 512 * KiB)], "Without it PCSX ReARMed uses its built-in HLE BIOS."),
        new("Saturn", Need.Optional, [Exact("saturn_bios.bin", 512 * KiB)],
            "Without it Yabause uses its HLE BIOS; playing a whole disc set needs it."),
        new("Sega CD", Need.OneOf, [Exact("bios_CD_U.bin", 128 * KiB), Exact("bios_CD_E.bin", 128 * KiB), Exact("bios_CD_J.bin", 128 * KiB)],
            "One regional BIOS (USA, Europe, Japan).", "Emulator:GenesisCorePath"),
        new("3DO", Need.OneOf,
            [Exact("panafz10.bin", MiB), Exact("panafz1.bin", MiB), Exact("panafz1j.bin", MiB), Exact("goldstar.bin", MiB), Exact("sanyotry.bin", MiB)],
            "Every one present must be valid.", "Emulator:OperaCorePath"),
        new("TurboGrafx-CD", Need.OneOf,
            [UpTo("syscard3.pce", MiB), UpTo("syscard3u.pce", MiB), UpTo("syscard2.pce", MiB), UpTo("syscard2u.pce", MiB), UpTo("syscard1.pce", MiB)],
            "A System Card; the first one found in this order is used."),
        new("TurboGrafx-CD (Games Express)", Need.Optional, [UpTo("gexpress.pce", MiB)], "Only Games Express discs need it."),
        new("PC-FX", Need.All, [Exact("pcfx.rom", MiB)], "Beetle PC-FX needs it for every game."),
        new("Commodore Amiga", Need.Optional, [Exact("kick34005.A500", 256 * KiB), Exact("kick40068.A1200", 512 * KiB)],
            "Without them PUAE uses its built-in AROS Kickstart; hard-disk games (.hdf, .hdz, .lha) need kick40068.A1200."),
        new(Cd32Group, Need.All, [UpTo("kick40060.CD32", MiB)],
            "PUAE needs a CD32 Kickstart for every game: the 512 KiB ROM with kick40060.CD32.ext, or the 1 MiB combined ROM."),
        new(Cd32ExtGroup, Need.Optional, [Exact("kick40060.CD32.ext", 512 * KiB)], "Needed unless kick40060.CD32 is the 1 MiB combined ROM."),
        new("Commodore CDTV", Need.All, [Exact("kick34005.A500", 256 * KiB), Exact("kick34005.CDTV", 256 * KiB)],
            "PUAE needs Kickstart 1.3 and the CDTV extended ROM for every game."),
        new("Arcade (FinalBurn Neo)", Need.Optional, [],
            "Board BIOS sets your games need (e.g. neogeo.zip, pgm.zip) go in this folder too; they're listed under Other files.",
            "Emulator:FbneoCorePath"),
    ];

    public static object Check(string root, IConfiguration config)
    {
        var folderExists = Directory.Exists(root);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cd32Kick = CheckFile(root, folderExists, Catalog.Single(g => g.System == Cd32Group).Files[0]);
        var groups = Catalog.Select(group =>
        {
            var files = group.Files.Select(file =>
            {
                known.Add(file.Name);
                return CheckFile(root, folderExists, file);
            }).ToList();
            var present = files.Where(f => f.Status != "Missing").ToList();
            var ok = present.All(f => f.Status == "Valid") && group.Need switch
            {
                Need.All => files.All(f => f.Status == "Valid"),
                Need.OneOf => present.Count > 0,
                _ => true,
            };
            // CD32 A11: as on the play page, a Kickstart of 512 KiB or less needs the extended ROM.
            if (group.System == Cd32ExtGroup)
                ok &= files[0].Status == "Valid" || cd32Kick is not { Status: "Valid", Size: <= 512 * KiB };
            return new
            {
                system = group.System, need = group.Need.ToString(), ok, note = group.Note, files,
                coreSetting = group.CoreSetting, coreEnv = group.CoreSetting?.Replace(":", "__"),
                coreConfigured = group.CoreSetting is { } key ? !string.IsNullOrWhiteSpace(config[key]) : (bool?)null,
            };
        }).ToList();

        var others = new List<object>();
        if (folderExists)
        {
            try
            {
                others.AddRange(new DirectoryInfo(root).EnumerateFiles()
                    .Where(f => !known.Contains(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new { name = f.Name, size = f.Length }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The catalog rows already show the folder as unreadable file by file.
            }
        }
        return new { folder = root, setting = "Paths:Bios", env = "Paths__Bios", folderExists, groups, otherFiles = others };
    }

    private static FileStatus CheckFile(string root, bool folderExists, BiosFile file)
    {
        FileStatus Result(string status, long? size = null, string? md5 = null) => new(file.Name, file.MinBytes, file.MaxBytes, status, size, md5);
        if (!folderExists)
            return Result("Missing");

        // The same checked open as GET /api/emulator/bios/{name}: links and case-ambiguous names are refused.
        var opened = ExternalRomPath.OpenRead(root, SafePath.Resolve(root, file.Name), FileShare.Read | FileShare.Delete);
        switch (opened.Status)
        {
            case ExternalPathStatus.Found:
                break;
            case ExternalPathStatus.Missing:
                return Result("Missing");
            case ExternalPathStatus.Unsafe:
                return Result("Unsafe");
            default:
                return Result("Unreadable");
        }
        using var stream = opened.Stream!;
        try
        {
            var size = stream.Length;
            if (size == 0)
                return Result("Missing", 0); // the play page treats an empty file as absent
            if (size < file.MinBytes || size > file.MaxBytes)
                return Result("WrongSize", size);
            return Result("Valid", size, Convert.ToHexStringLower(MD5.HashData(stream)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result("Unreadable");
        }
    }
}
