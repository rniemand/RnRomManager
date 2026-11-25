using System.Security.Cryptography;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>GET /api/admin/bios</c> (mapped in <see cref="AdminEndpoints"/>): every BIOS file the play page loads from <c>Paths:Bios</c>, whether it's there and
/// whether its size passes, so an Admin can check a deployment. The sizes mirror the play page's checks
/// (<c>static/emulator/params.js</c>) and <c>docs/configuration.md</c>; keep them in step.
/// </summary>
public static class BiosInfo
{
    /// <summary>All: every file is needed. OneOf: at least one. Optional: none (play falls back or skips a feature).</summary>
    public enum Need { All, OneOf, Optional }

    public sealed record BiosFile(string Name, long MinBytes, long MaxBytes);

    /// <param name="CoreSetting">The operator-core setting this system also needs, if any.</param>
    public sealed record Group(string System, Need Need, BiosFile[] Files, string Note, string? CoreSetting = null);

    private const long KiB = 1024, MiB = 1024 * 1024;
    private static BiosFile Exact(string name, long bytes) => new(name, bytes, bytes);
    private static BiosFile AtLeast(string name, long bytes) => new(name, bytes, MiB * 16);
    private static BiosFile UpTo(string name, long bytes) => new(name, 1, bytes);

    public static readonly Group[] Catalog =
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
        new("Arcade (FinalBurn Neo)", Need.Optional, [],
            "Board BIOS sets your games need (e.g. neogeo.zip, pgm.zip) go in this folder too; they're listed under Other files.",
            "Emulator:FbneoCorePath"),
    ];

    public static object Check(string root, IConfiguration config)
    {
        var folderExists = Directory.Exists(root);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    public sealed record FileStatus(string Name, long MinBytes, long MaxBytes, string Status, long? Size, string? Md5);

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
