using System.Security.Cryptography;
using System.Text;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Storage;

namespace RnRomManager.TestData;

/// <summary>
/// Writes clrmamepro DATs for generated ROMs (spec §15.2): per system the no-intro DAT plus the 4 metadat files,
/// laid out like a refresh leaves <c>cache/dats/</c>, and a matching <c>dats.json</c>. The app then loads them
/// exactly as it would real DATs.
/// </summary>
public static class FixtureDatWriter
{
    public const string Version = "fixture-1";

    /// <param name="datsDirectory">The <c>cache/dats/</c> folder (created if missing).</param>
    /// <param name="gamesByDatName">Keyed by handler <c>DatName</c>, e.g. <c>Nintendo - Game Boy</c>.</param>
    public static void Write(
        string datsDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<FixtureGame>> gamesByDatName,
        string version = Version,
        DateTimeOffset? downloadedAt = null)
    {
        var when = downloadedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var manifest = new List<DatManifestEntry>();
        foreach (var (datName, games) in gamesByDatName)
        {
            manifest.Add(WriteFile(datsDirectory, DatKind.NoIntro, datName, version, when, NoIntro(datName, version, games)));
            manifest.Add(WriteFile(datsDirectory, DatKind.ReleaseYear, datName, null, when,
                Metadat(datName, DatKind.ReleaseYear, games, g => g.ReleaseYear is { } y ? [y.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [])));
            manifest.Add(WriteFile(datsDirectory, DatKind.Developer, datName, null, when, Metadat(datName, DatKind.Developer, games, g => g.Developers)));
            manifest.Add(WriteFile(datsDirectory, DatKind.Publisher, datName, null, when, Metadat(datName, DatKind.Publisher, games, g => g.Publishers)));
            manifest.Add(WriteFile(datsDirectory, DatKind.Genre, datName, null, when, Metadat(datName, DatKind.Genre, games, g => g.Genres)));
        }
        DatManifest.Write(Path.Combine(datsDirectory, DatManifest.FileName), manifest);
    }

    private static DatManifestEntry WriteFile(string root, string kind, string datName, string? version, DateTimeOffset when, string text)
    {
        var relative = DatKind.RelativePath(kind, datName);
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bytes);
        return new DatManifestEntry(relative, datName, version, when, Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    private static string NoIntro(string datName, string version, IEnumerable<FixtureGame> games)
    {
        var text = new StringBuilder();
        text.Append($"clrmamepro (\n\tname \"{datName}\"\n\tdescription \"{datName}\"\n\tversion \"{version}\"\n)\n\n");
        foreach (var game in games)
        {
            // Upper-case hashes like the real libretro DATs.
            text.Append($"game (\n\tname \"{game.GameName}\"\n\trom ( name \"{game.RomName}\" size {game.Rom.Length} ")
                .Append($"crc {Crc32.ToHex(game.Rom).ToUpperInvariant()} md5 {Convert.ToHexString(MD5.HashData(game.Rom))} ")
                .Append($"sha1 {Convert.ToHexString(SHA1.HashData(game.Rom))} )\n)\n");
        }
        return text.ToString();
    }

    private static string Metadat(string datName, string field, IEnumerable<FixtureGame> games, Func<FixtureGame, IEnumerable<string>> values)
    {
        var text = new StringBuilder($"clrmamepro (\n\tname \"{datName}\"\n\tdescription \"{datName}\"\n)\n\n");
        foreach (var game in games)
        {
            foreach (var value in values(game))
            {
                text.Append($"game (\n\tcomment \"{game.GameName}\"\n\t{field} \"{value}\"\n")
                    .Append($"\trom ( crc {Crc32.ToHex(game.Rom).ToUpperInvariant()} )\n)\n\n");
            }
        }
        return text.ToString();
    }
}
