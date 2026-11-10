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
    public const string SnesDat = "Nintendo - Super Nintendo Entertainment System";
    public const string Atari2600Dat = "Atari - 2600";
    public const string ColecoVisionDat = "Coleco - ColecoVision";
    public const string GameGearDat = "Sega - Game Gear";
    public const string MasterSystemDat = "Sega - Master System - Mark III";
    public const string MegaDriveDat = "Sega - Mega Drive - Genesis";
    public const string Sega32xDat = "Sega - 32X";
    public const string TurboGrafx16Dat = "NEC - PC Engine - TurboGrafx 16";

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
        Directory.CreateDirectory(Path.Combine(ingest, "nes"));
        Directory.CreateDirectory(Path.Combine(ingest, "snes"));
        Directory.CreateDirectory(Path.Combine(ingest, "a2600"));
        Directory.CreateDirectory(Path.Combine(ingest, "coleco"));
        Directory.CreateDirectory(Path.Combine(ingest, "gg"));
        Directory.CreateDirectory(Path.Combine(ingest, "sms"));
        Directory.CreateDirectory(Path.Combine(ingest, "md"));
        Directory.CreateDirectory(Path.Combine(ingest, "s32x"));
        Directory.CreateDirectory(Path.Combine(ingest, "tg16"));
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
        var pioneer = new FixtureGame("Pixel Pioneer (USA)", "Pixel Pioneer (USA).nes", new SyntheticNesRomBuilder { Seed = 1001, Mapper = 1, Battery = true }.Build())
        { ReleaseYear = 1987, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Platform"] };
        Loose("nes", "01-pixel-pioneer.nes", pioneer.Rom);
        var modeSeven = new SyntheticSnesRomBuilder { Seed = 1101, Mapping = SnesMapping.LoROM, Title = "MODE SEVEN RALLY", SramSizeByte = 3 };
        var rally = new FixtureGame("Mode Seven Rally (USA)", "Mode Seven Rally (USA).sfc", modeSeven.Build())
        { ReleaseYear = 1992, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] };
        // Copier-headered on disk, so ingestion must strip it to match the DAT's .sfc entry (S3).
        Loose("snes", "01-mode-seven-rally.smc", (modeSeven with { CopierHeader = true }).Build());
        var laserRom = new byte[4096];
        new Random(1201).NextBytes(laserRom);
        var lanes = new FixtureGame("Laser Lanes (USA)", "Laser Lanes (USA).a26", laserRom)
        { ReleaseYear = 1982, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Shooter"] };
        // A loose .bin: ingestion must accept it and store the DAT's .a26 name (A3).
        Loose("a2600", "01-laser-lanes.bin", laserRom);
        var pursuitRom = new byte[8192];
        new Random(1301).NextBytes(pursuitRom);
        (pursuitRom[0], pursuitRom[1]) = (0xAA, 0x55);
        var pursuit = new FixtureGame("Pixel Pursuit (USA)", "Pixel Pursuit (USA).col", pursuitRom)
        { ReleaseYear = 1983, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Action"] };
        // A loose .rom: ingestion must accept it and store the DAT's .col name (C4).
        Loose("coleco", "01-pixel-pursuit.rom", pursuitRom);
        var driftRom = new byte[32768];
        new Random(1401).NextBytes(driftRom);
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(driftRom, 0x7FF0);
        driftRom[0x7FFF] = 0x6C; // GG Export, size code 12
        var drift = new FixtureGame("Pocket Drift (World)", "Pocket Drift (World).gg", driftRom)
        { ReleaseYear = 1992, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] };
        // Its TMR SEGA header (G3) shows on the release; the match proves .gg ingestion.
        Loose("gg", "01-pocket-drift.gg", driftRom);
        var harborRom = new byte[32768];
        new Random(1501).NextBytes(harborRom);
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(harborRom, 0x7FF0);
        harborRom[0x7FFF] = 0x4C; // SMS Export, size code 12
        var harbor = new FixtureGame("Harbor Patrol (World)", "Harbor Patrol (World).sms", harborRom)
        { ReleaseYear = 1988, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Action"] };
        Loose("sms", "01-harbor-patrol.sms", harborRom);
        var cometRom = new SyntheticMegaDriveRomBuilder { Seed = 1601, Size = 524288 }.Build();
        var comet = new FixtureGame("Turbo Comet (USA, Europe)", "Turbo Comet (USA, Europe).md", cometRom)
        { ReleaseYear = 1991, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Racing"] };
        // A loose .gen: ingestion must accept it and store the DAT's .md name (D3).
        Loose("md", "01-turbo-comet.gen", cometRom);
        var relayRom = new SyntheticMegaDriveRomBuilder { Seed = 1701, Size = 524288, SystemType = "SEGA 32X" }.Build();
        var relay = new FixtureGame("Star Relay (USA)", "Star Relay (USA).32x", relayRom)
        { ReleaseYear = 1995, Developers = ["Seed Studio"], Publishers = ["Fixture Works"], Genres = ["Shooter"] };
        Loose("s32x", "01-star-relay.32x", relayRom);

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
        FixtureDatWriter.Write(Path.Combine(cache, "dats"), new Dictionary<string, IReadOnlyList<FixtureGame>> { [GbDat] = gb, [GbcDat] = gbc, [GbaDat] = [courier], [N64Dat] = [kart], [NesDat] = [pioneer], [SnesDat] = [rally], [Atari2600Dat] = [lanes], [ColecoVisionDat] = [pursuit], [GameGearDat] = [drift], [MasterSystemDat] = [harbor], [MegaDriveDat] = [comet], [Sega32xDat] = [relay], [TurboGrafx16Dat] = [] });
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
