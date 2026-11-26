using System.Security.Cryptography;
using System.Text;
using RnRomManager.Core.Dats;

namespace RnRomManager.TestData;

/// <summary>
/// Writes clrmamepro DATs for generated ROMs (spec §15.2): per system the main DAT plus its metadat files (by default
/// <c>no-intro</c> and all four; <see cref="FixtureDatLayout"/> overrides it per DatName, B1/B2),
/// laid out like a refresh leaves <c>cache/dats/</c>, and a matching <c>dats.json</c>. The app then loads them
/// exactly as it would real DATs.
/// </summary>
public static class FixtureDatWriter
{
    public const string Version = "fixture-1";

    /// <summary>A titledb manifest version: T3 requires <c>yyyy.MM.dd</c>.</summary>
    public const string TitleDbVersion = "2026.01.01";

    /// <summary>A MAME manifest version, in Arcade D4's download-time format.</summary>
    public const string MameVersion = "2026-01-01T00:00:00Z";

    /// <param name="datsDirectory">The <c>cache/dats/</c> folder (created if missing).</param>
    /// <param name="gamesByDatName">Keyed by handler <c>DatName</c>, e.g. <c>Nintendo - Game Boy</c>.</param>
    /// <param name="mame">Arcade D4: DatName → MAME XML text, written to <c>mame/&lt;DatName&gt;.xml</c>.</param>
    public static void Write(
        string datsDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<FixtureGame>> gamesByDatName,
        string version = Version,
        DateTimeOffset? downloadedAt = null,
        IReadOnlyDictionary<string, IReadOnlyList<TitleDbRow>>? titleDb = null,
        string titleDbVersion = TitleDbVersion,
        IReadOnlyDictionary<string, FixtureDatLayout>? layouts = null,
        IReadOnlyDictionary<string, string>? mame = null)
    {
        var when = downloadedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var manifest = new List<DatManifestEntry>();
        foreach (var (datName, games) in gamesByDatName)
        {
            var layout = layouts?.GetValueOrDefault(datName) ?? FixtureDatLayout.NoIntro;
            manifest.Add(WriteFile(datsDirectory, layout.DatFolder, datName, version, when, NoIntro(datName, version, games)));
            foreach (var kind in layout.MetadatKinds)
            {
                manifest.Add(WriteFile(datsDirectory, kind, datName, null, when, kind switch
                {
                    DatKind.ReleaseYear => Metadat(datName, kind, games,
                        g => g.ReleaseYear is { } y ? [y.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []),
                    DatKind.Developer => Metadat(datName, kind, games, g => g.Developers),
                    DatKind.Publisher => Metadat(datName, kind, games, g => g.Publishers),
                    DatKind.Genre => Metadat(datName, kind, games, g => g.Genres),
                    _ => throw new ArgumentException($"Unknown metadat kind '{kind}'.", nameof(layouts)),
                }));
            }
        }
        // titledb systems (T2): one compact file each, written by the app's own serializer.
        foreach (var (datName, rows) in titleDb ?? new Dictionary<string, IReadOnlyList<TitleDbRow>>())
        {
            using var compact = new MemoryStream();
            TitleDb.WriteCompactAsync(compact, rows, default).GetAwaiter().GetResult();
            manifest.Add(WriteBytes(datsDirectory, TitleDb.RelativePath(datName), datName, titleDbVersion, when, compact.ToArray()));
        }
        foreach (var (datName, xml) in mame ?? new Dictionary<string, string>())
            manifest.Add(WriteBytes(datsDirectory, MameDat.RelativePath(datName), datName, MameVersion, when, Encoding.UTF8.GetBytes(xml)));
        DatManifest.Write(Path.Combine(datsDirectory, DatManifest.FileName), manifest);
    }

    private static DatManifestEntry WriteFile(
        string root,
        string kind,
        string datName,
        string? version,
        DateTimeOffset when,
        string text) =>
        WriteBytes(root, DatKind.RelativePath(kind, datName), datName, version, when, Encoding.UTF8.GetBytes(text));

    private static DatManifestEntry WriteBytes(
        string root,
        string relative,
        string datName,
        string? version,
        DateTimeOffset when,
        byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            var hashes = game.RomHashes;
            text.Append($"game (\n\tname \"{game.GameName}\"\n\trom ( name \"{game.RomName}\" size {hashes.SizeBytes} ")
                .Append($"crc {hashes.Crc32.ToUpperInvariant()} md5 {hashes.Md5.ToUpperInvariant()} ")
                .Append($"sha1 {hashes.Sha1.ToUpperInvariant()}");
            if (game.Serial is { } serial)
                text.Append($" serial \"{serial}\"");
            text.Append(" )\n)\n");
        }
        return text.ToString();
    }

    private static string Metadat(
        string datName,
        string field,
        IEnumerable<FixtureGame> games,
        Func<FixtureGame, IEnumerable<string>> values)
    {
        var text = new StringBuilder($"clrmamepro (\n\tname \"{datName}\"\n\tdescription \"{datName}\"\n)\n\n");
        foreach (var game in games)
        {
            foreach (var value in values(game))
            {
                text.Append($"game (\n\tcomment \"{game.GameName}\"\n\t{field} \"{value}\"\n")
                    .Append($"\trom ( crc {game.RomHashes.Crc32.ToUpperInvariant()} )\n)\n\n");
            }
        }
        return text.ToString();
    }
}
