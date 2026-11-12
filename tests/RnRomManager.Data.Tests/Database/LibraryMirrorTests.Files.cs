using System.Security.Cryptography;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Disc systems B12/B13: <c>rom_files</c> mirror rows, claims and lookups.</summary>
public partial class LibraryMirrorTests
{
    private static InfoJsonRomFile SetFile(string name, int seed)
    {
        var bytes = RomBytes(seed);
        return new InfoJsonRomFile(name, name, name, bytes.Length, Crc32.ToHex(bytes),
            Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    /// <summary>One disc: data track (main) + cue + Track 2, every file named exactly.</summary>
    private static InfoJsonRom Disc(string title, int seed, int audioSeed, bool isPrimary = true)
    {
        var track = $"{title} (Track 1).bin";
        return Rom(track, RomBytes(seed), isPrimary) with
        {
            ExternalFileName = track,
            Files = [SetFile($"{title}.cue", seed + 100), SetFile($"{title} (Track 2).bin", audioSeed)],
        };
    }

    private static InfoJsonGame SetGame(string title, params InfoJsonRom[] discs) =>
        Game(title, discs) with { System = ExternalSetTestHandler.SystemId };

    [Test]
    public async Task Files_AreWritten_AndCascadeDeleted()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA)", 1, 50));

        await _mirror.ReplaceAsync(Entry(game, "extset/D/Disc Runner.zip"), CancellationToken.None);

        var rom = (await _roms.ListByGameAsync(game.GameId, CancellationToken.None)).Single();
        var files = await _roms.ListFilesByGameAsync(game.GameId, CancellationToken.None);
        var track2 = game.Roms[0].Files![1];
        Assert.That(files.Select(f => (f.RomId, f.RomSha1, f.Ordinal, f.ExternalFileName)), Is.EqualTo(new[]
        {
            (rom.Id, rom.Sha1, 0, "Disc Runner (USA).cue"),
            (rom.Id, rom.Sha1, 1, "Disc Runner (USA) (Track 2).bin"),
        }));
        Assert.That(files[1] with { Id = 0 }, Is.EqualTo(new RomFileRow
        {
            RomId = rom.Id, GameId = game.GameId, RomSha1 = rom.Sha1, Ordinal = 1, FileName = track2.FileName,
            ExternalFileName = track2.ExternalFileName, OriginalFileName = track2.OriginalFileName,
            SizeBytes = track2.SizeBytes, Crc32 = track2.Crc32, Md5 = track2.Md5, Sha1 = track2.Sha1,
        }));

        await _mirror.ApplyAsync([], [game.GameId], CancellationToken.None);

        Assert.That(await _roms.ListFilesByGameAsync(game.GameId, CancellationToken.None), Is.Empty);
        Assert.That(await _roms.ListFilesBySha1Async(track2.Sha1, CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task ListExternal_IncludesSetFiles_MainFileFirst()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA) (Disc 1)", 1, 50), Disc("Disc Runner (USA) (Disc 2)", 2, 51, isPrimary: false));
        await _mirror.ReplaceAsync(Entry(game, "extset/D/Disc Runner.zip"), CancellationToken.None);
        var files = await _roms.ListFilesByGameAsync(game.GameId, CancellationToken.None);

        var rows = await _roms.ListExternalAsync(CancellationToken.None);

        Assert.That(rows.Select(r => (r.ExternalFileName, r.FileId)), Is.EqualTo(new[]
        {
            ("Disc Runner (USA) (Disc 1) (Track 1).bin", (long?)null),
            ("Disc Runner (USA) (Disc 1).cue", files[0].Id),
            ("Disc Runner (USA) (Disc 1) (Track 2).bin", files[1].Id),
            ("Disc Runner (USA) (Disc 2) (Track 1).bin", null),
            ("Disc Runner (USA) (Disc 2).cue", files[2].Id),
            ("Disc Runner (USA) (Disc 2) (Track 2).bin", files[3].Id),
        }));
        Assert.That(rows.Select(r => r.ZipPath), Is.All.EqualTo("extset/D/Disc Runner.zip"));
        Assert.That(rows[2].Sha1, Is.EqualTo(game.Roms[0].Files![1].Sha1));
    }

    [Test]
    public async Task TwoReleases_MayShareATrackSha1()
    {
        // A silent audio track (same seed) on discs of two games.
        var a = SetGame("Alpha", Disc("Alpha (USA)", 1, 77));
        var b = SetGame("Beta", Disc("Beta (USA)", 2, 77));

        await _mirror.ReplaceAsync(Entry(a, "extset/A/Alpha.zip"), CancellationToken.None);
        await _mirror.ReplaceAsync(Entry(b, "extset/B/Beta.zip"), CancellationToken.None);

        var shared = await _roms.ListFilesBySha1Async(a.Roms[0].Files![1].Sha1, CancellationToken.None);
        Assert.That(shared.Select(f => (f.GameId, f.ExternalFileName)), Is.EqualTo(new[]
        {
            (a.GameId, "Alpha (USA) (Track 2).bin"),
            (b.GameId, "Beta (USA) (Track 2).bin"),
        }));
    }

    [Test]
    public async Task GetFile_IsNullForAnotherReleaseOrGame()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA) (Disc 1)", 1, 50), Disc("Disc Runner (USA) (Disc 2)", 2, 51, isPrimary: false));
        var other = SetGame("Other", Disc("Other (USA)", 3, 52));
        await _mirror.ReplaceAsync(Entry(game, "extset/D/Disc Runner.zip"), CancellationToken.None);
        await _mirror.ReplaceAsync(Entry(other, "extset/O/Other.zip"), CancellationToken.None);
        var files = await _roms.ListFilesByGameAsync(game.GameId, CancellationToken.None);
        var (disc1, disc2) = (files[0].RomId, files[2].RomId);

        Assert.That((await _roms.GetFileAsync(game.GameId, disc1, files[1].Id, CancellationToken.None))!.ExternalFileName,
            Is.EqualTo("Disc Runner (USA) (Disc 1) (Track 2).bin"));
        Assert.That(await _roms.GetFileAsync(game.GameId, disc2, files[1].Id, CancellationToken.None), Is.Null, "the other release");
        Assert.That(await _roms.GetFileAsync(other.GameId, disc1, files[1].Id, CancellationToken.None), Is.Null, "another game");
    }

    [Test]
    public async Task Sync_RestoresRomFilesFromTheZip()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA)", 1, 50));
        // Hand-made: only info.json, as an external game's ZIP holds no roms/ entry.
        var path = Abs("extset/D/Disc Runner.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry(LibraryZip.InfoJsonEntry).Open();
            entry.Write(InfoJsonSerializer.Serialize(game));
        }

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Skipped, Is.Empty);
        Assert.That((await _roms.ListFilesByGameAsync(game.GameId, CancellationToken.None)).Select(f => f.ExternalFileName),
            Is.EqualTo(new[] { "Disc Runner (USA).cue", "Disc Runner (USA) (Track 2).bin" }));
    }
}
