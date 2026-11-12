using System.Security.Cryptography;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B9–B11: FileSets systems' single files, exact names and archives (<see cref="ExternalSetTestHandler"/>).</summary>
public partial class ExternalLibraryTests
{
    private static readonly byte[] DiscIso = RandomNumberGenerator.GetBytes(3072);
    private static readonly byte[] DiscRev1 = RandomNumberGenerator.GetBytes(3000);
    private static readonly byte[] DarkIso = RandomNumberGenerator.GetBytes(2500);
    private static readonly byte[] TrackData = RandomNumberGenerator.GetBytes(2352 * 2);
    private static readonly byte[] SetData = RandomNumberGenerator.GetBytes(2352 * 3);
    private static readonly byte[] SetData2 = RandomNumberGenerator.GetBytes(2352 * 3 + 16);
    private static readonly byte[] Audio = RandomNumberGenerator.GetBytes(2352);
    private static readonly byte[] Audio2 = RandomNumberGenerator.GetBytes(2352 + 8);

    /// <summary>The <see cref="ExternalSetTestHandler"/> fixture Redump DAT: one rom per game, as in Redump.</summary>
    private static IReadOnlyList<FixtureGame> SetDatGames =>
    [
        new FixtureGame("Disc Game (USA)", "Disc Game (USA).iso", DiscIso),
        // Same rom name: a later release of the same game, and another game in the same letter folder.
        new FixtureGame("Disc Game (USA) (Rev 1)", "Disc Game (USA).iso", DiscRev1),
        new FixtureGame("Dark Game (USA)", "Disc Game (USA).iso", DarkIso),
        new FixtureGame("Track Game (USA)", "Track Game (USA) (Track 1).bin", TrackData),
        new FixtureGame("Set Game (USA)", "Set Game (USA) (Track 1).bin", SetData),
        new FixtureGame("Set Game (USA) (Disc 2)", "Set Game (USA) (Disc 2) (Track 1).bin", SetData2),
    ];

    private string SetIngest => Path.Combine(_factory.Root, "ingest", ExternalSetTestHandler.SystemId);

    private string SetFolder(char bucket) => Path.Combine(_factory.Root, "roms", ExternalSetTestHandler.SystemId, bucket.ToString());

    private IngestFile DropSet(string name, byte[] bytes)
    {
        var path = Path.Combine(SetIngest, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var info = new FileInfo(path);
        return new IngestFile(ExternalSetTestHandler.SystemId, path, name, info.Length, info.LastWriteTimeUtc);
    }

    private InfoJsonGame ReadSetGame(string zipPath)
    {
        using var zip = LibraryZip.Open(zipPath, Service<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    [Test]
    public async Task FileSets_MatchedIso_IsStoredBesideTheZip_UnderItsExactName()
    {
        var file = DropSet("dump.iso", DiscIso);

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var folder = SetFolder('D');
        Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName).Order(), Is.EqualTo(new[] { "Disc Game (USA).iso", "Disc Game.zip" }));
        Assert.That(File.ReadAllBytes(Path.Combine(folder, "Disc Game (USA).iso")), Is.EqualTo(DiscIso));
        var rom = ReadSetGame(Path.Combine(folder, "Disc Game.zip")).Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.Files), Is.EqualTo(("Disc Game (USA).iso", "Disc Game (USA).iso", (IReadOnlyList<InfoJsonRomFile>?)null)));
    }

    [Test]
    public async Task FileSets_MatchedDataTrack_TakesItsTagsFromTheGameName()
    {
        var result = await IngestAsync(Pipeline(), DropSet("track.bin", TrackData));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var rom = ReadSetGame(Path.Combine(SetFolder('T'), "Track Game.zip")).Roms.Single();
        Assert.That((rom.ExternalFileName, rom.BaseTitle), Is.EqualTo(("Track Game (USA) (Track 1).bin", "Track Game")));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "USA" }));
        Assert.That(rom.Flags, Is.Empty, "no Track 1 flag");
    }

    [Test]
    public async Task FileSets_SameFileNameInTheFolder_ForAnotherGame_IsNameTaken_AndTheFileStays()
    {
        Assert.That(Code(await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var second = DropSet("dark.iso", DarkIso);

        var result = await IngestAsync(Pipeline(), second);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.NameTaken)));
        Assert.That(File.ReadAllBytes(second.FullPath), Is.EqualTo(DarkIso));
        Assert.That(Directory.GetFiles(SetFolder('D')).Select(Path.GetFileName).Order(), Is.EqualTo(new[] { "Disc Game (USA).iso", "Disc Game.zip" }));
    }

    [Test]
    public async Task FileSets_NameOfAnExistingReleaseOfTheSameGame_IsNameTaken_WithoutASuffix()
    {
        await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso));
        var rev1 = DropSet("rev1.iso", DiscRev1);

        var result = await IngestAsync(Pipeline(), rev1);

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Error, IngestErrors.NameTaken)));
        Assert.That(File.Exists(rev1.FullPath), Is.True);
        Assert.That(ReadSetGame(Path.Combine(SetFolder('D'), "Disc Game.zip")).Roms.Select(r => r.FileName), Is.EqualTo(new[] { "Disc Game (USA).iso" }));
        Assert.That(Directory.GetFiles(SetFolder('D')).Select(Path.GetFileName), Has.None.Contain("(2)"));
    }

    [Test]
    public async Task FileSets_UnmatchedIso_GoesToReview()
    {
        var file = DropSet("unknown.iso", RandomNumberGenerator.GetBytes(2048));

        var result = await IngestAsync(Pipeline(), file);

        Assert.That(result?.Outcome, Is.EqualTo(IngestOutcome.Review));
        Assert.That(File.Exists(file.FullPath), Is.True);
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<(string, string)>("SELECT system_id, reason FROM ingest_review_items"),
            Is.EqualTo((ExternalSetTestHandler.SystemId, "NoDatMatch")));
    }

    [Test]
    public async Task FileSets_Archive_IsSkipped_AndStays()
    {
        var path = Path.Combine(SetIngest, "pack.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("Disc Game (USA).iso").Open();
            stream.Write(DiscIso);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        await Job(Pipeline()).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

        var item = _items.Single(i => i.SystemId == ExternalSetTestHandler.SystemId);
        Assert.That((item.Outcome, item.Reason, item.ArchiveEntry), Is.EqualTo((IngestOutcome.Skipped, IngestReason.ArchiveNotSupported, (string?)null)));
        Assert.That(File.Exists(path), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(_factory.Root, "roms", ExternalSetTestHandler.SystemId), "*", SearchOption.AllDirectories), Is.Empty);
    }
}
