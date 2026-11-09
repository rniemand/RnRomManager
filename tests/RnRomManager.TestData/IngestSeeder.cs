using System.IO.Compression;

namespace RnRomManager.TestData;

/// <summary>Deterministic synthetic ingestion sets and matching DATs (§15.2). No external downloads.</summary>
public static class IngestSeeder
{
    public const string GbDat = "Nintendo - Game Boy";
    public const string GbcDat = "Nintendo - Game Boy Color";
    public const string GbaDat = "Nintendo - Game Boy Advance";
    public const string N64Dat = "Nintendo - Nintendo 64";
    public const string NesDat = "Nintendo - Nintendo Entertainment System";

    public static void Seed(string ingest, string cache, SeedSet set, TimeProvider time)
    {
        ValidatePaths(ingest, cache);
        if (!Enum.IsDefined(set)) throw new ArgumentOutOfRangeException(nameof(set));
        ingest = Path.GetFullPath(ingest);
        cache = Path.GetFullPath(cache);
        Directory.CreateDirectory(Path.Combine(ingest, "gb"));
        Directory.CreateDirectory(Path.Combine(ingest, "gbc"));
        Directory.CreateDirectory(Path.Combine(ingest, "gba"));
        Directory.CreateDirectory(Path.Combine(ingest, "n64"));
        var files = new List<string>();
        var gb = new List<FixtureGame>();
        var gbc = new List<FixtureGame>();

        FixtureGame Game(string name, int seed, byte cgb = 0) => new(name, name + (cgb == 0 ? ".gb" : ".gbc"),
            new SyntheticRomBuilder { Seed = seed, Title = cgb == 0 ? "ORBITBLOCKS" : "COLORDEMO", CgbFlag = cgb }.Build())
        { ReleaseYear = cgb == 0 ? 1990 : 1999, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Puzzle"] };

        void Loose(string system, string name, byte[] bytes)
        {
            var path = Path.Combine(ingest, system, name);
            File.WriteAllBytes(path, bytes);
            files.Add(path);
        }

        var world = Game("Orbit Blocks (World)", 3101);
        var japan = Game("Orbit Blocks (Japan)", 3102);
        var revision = Game("Orbit Blocks (World) (Rev 1)", 3103);
        var quest = Game("Quest, The (USA, Europe)", 3104);
        var dual = Game("Color Garden (World)", 3105, 0x80);
        var color = Game("Color Racer (USA)", 3106, 0xC0);
        gb.AddRange([world, japan, revision, quest, new FixtureGame("Seven Zip Demo (World)", TestFixtures.Basic7zEntry, TestFixtures.Basic7zRom())]);
        gbc.AddRange([dual, color]);
        Loose("gb", "01-orbit-world.gb", world.Rom);
        Loose("gb", "02-orbit-japan.gb", japan.Rom);
        Loose("gb", "03-orbit-revision.gb", revision.Rom);
        Loose("gb", "04-color-garden.gbc", dual.Rom); // dual-mode deliberately dropped in the GB folder
        Loose("gbc", "01-color-racer.gbc", color.Rom);
        var courier = new FixtureGame("Star Courier (USA)", "Star Courier (USA).gba",
            new SyntheticGbaRomBuilder { Seed = 3301, Title = "STARCOURIER", GameCode = "ASCE", MakerCode = "01" }.Build())
        { ReleaseYear = 2002, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Action"] };
        Loose("gba", "01-star-courier.gba", courier.Rom);
        var orbitKart = new SyntheticN64RomBuilder { Seed = 6401, Title = "ORBIT KART 64", GameCode = "NOKE" };
        var kart = new FixtureGame("Orbit Kart 64 (USA)", "Orbit Kart 64 (USA).z64", orbitKart.Build())
        { ReleaseYear = 1998, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] };
        // Byte-swapped on disk, so ingestion must convert it to match the DAT's .z64 entry (N3).
        Loose("n64", "01-orbit-kart.v64", (orbitKart with { ByteOrder = N64ByteOrder.V64 }).Build());

        var zipPath = Path.Combine(ingest, "gb", "05-quest.zip");
        using (var file = File.Create(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("games/" + quest.RomName, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(quest.Rom);
        }
        files.Add(zipPath);
        var sevenZip = Path.Combine(ingest, "gb", "06-sevenzip.7z");
        File.Copy(TestFixtures.Basic7zPath, sevenZip, overwrite: true);
        files.Add(sevenZip);
        Loose("gb", "90-duplicate.gb", world.Rom);
        Loose("gb", "91-unmatched.gb", new SyntheticRomBuilder { Seed = 3191, Title = "ORBITBLOCKS" }.Build());
        // A second review item with nothing like it in the DATs (no suggestions); e2e deletes it.
        Loose("gb", "93-mystery.gb", new SyntheticRomBuilder { Seed = 3193, Title = "MYSTERYCART" }.Build());
        Loose("gb", "92-invalid.gb", new SyntheticRomBuilder { Seed = 3192, CorruptLogo = true, CorruptHeaderChecksum = true }.Build());

        if (set == SeedSet.Full)
        {
            for (var i = 1; i <= 200; i++)
            {
                var isColor = i % 2 == 0;
                var game = Game($"Generated Game {i:D3} (World)", 3200 + i, isColor ? (byte)0xC0 : (byte)0);
                (isColor ? gbc : gb).Add(game);
                Loose(isColor ? "gbc" : "gb", $"20-generated-{i:D3}" + (isColor ? ".gbc" : ".gb"), game.Rom);
            }
        }
        FixtureDatWriter.Write(Path.Combine(cache, "dats"), new Dictionary<string, IReadOnlyList<FixtureGame>> { [GbDat] = gb, [GbcDat] = gbc, [GbaDat] = [courier], [N64Dat] = [kart], [NesDat] = [] });
        // Ready for an immediate ingest, including in tests: no TooRecent items (§7.1).
        foreach (var path in files) File.SetLastWriteTimeUtc(path, time.GetUtcNow().AddMinutes(-1).UtcDateTime);
    }

    public static void ValidatePaths(string ingest, string cache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingest);
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ingest));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cache));
        if (a == Path.GetPathRoot(a) || b == Path.GetPathRoot(b))
            throw new ArgumentException("Ingest and cache must not be filesystem roots.");
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ingest and cache must be distinct, non-nested folders.");
    }
}
