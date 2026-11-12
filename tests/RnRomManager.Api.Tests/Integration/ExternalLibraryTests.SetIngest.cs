using System.Security.Cryptography;
using System.Text;
using Dapper;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B7/B7a: a planned bin/cue set stored, queued for Review, or rolled back as one unit.</summary>
public partial class ExternalLibraryTests
{
    private const string SetCue = "Set Game (USA).cue";
    private const string SetTrack1 = "Set Game (USA) (Track 1).bin";
    private const string SetTrack2 = "Set Game (USA) (Track 2).bin";

    /// <summary>Writes a cue naming <paramref name="tracks"/> in order, and each track, a minute old.</summary>
    private string[] DropDisc(string cue, params (string Name, byte[] Bytes)[] tracks)
    {
        var text = string.Concat(tracks.Select((t, i) => $"FILE \"{t.Name}\" BINARY\n  TRACK {i + 1:00} {(i == 0 ? "MODE1/2352" : "AUDIO")}\n    INDEX 01 00:00:00\n"));
        var files = new List<(string, byte[])> { (cue, Encoding.UTF8.GetBytes(text)) };
        files.AddRange(tracks);
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(SetIngest, name);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        }
        return [.. files.Select(f => f.Item1)];
    }

    private string[] DropSetGame() => DropDisc(SetCue, (SetTrack1, SetData), (SetTrack2, Audio));

    private Task RunJobAsync(IngestPipeline pipeline) =>
        Job(pipeline).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);

    private List<(string Name, string Outcome, string? Reason)> SetItems() =>
        [.. _items.Where(i => i.SystemId == ExternalSetTestHandler.SystemId).Select(i => (Path.GetFileName(i.SourcePath), i.Outcome, i.Reason))];

    private string[] IngestLeft() => [.. Directory.GetFiles(SetIngest).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    [Test]
    public async Task Set_OneMatch_IsOneRelease_WithItsFilesInCueOrder()
    {
        var dropped = DropSetGame();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Ingested, (string?)null))));
        var folder = SetFolder('S');
        Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            Is.EqualTo(new[] { SetTrack1, SetTrack2, SetCue, "Set Game.zip" }));
        Assert.That(File.ReadAllBytes(Path.Combine(folder, SetTrack2)), Is.EqualTo(Audio));
        Assert.That(IngestLeft(), Is.Empty);

        var rom = ReadSetGame(Path.Combine(folder, "Set Game.zip")).Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.Metadata.Sha1), Is.EqualTo((SetTrack1, SetTrack1, Convert.ToHexStringLower(SHA1.HashData(SetData)))));
        Assert.That(rom.Flags, Is.Empty);
        Assert.That(rom.Files!.Select(f => (f.FileName, f.ExternalFileName, f.OriginalFileName)), Is.EqualTo(new[]
        {
            (SetCue, SetCue, SetCue),
            (SetTrack2, SetTrack2, SetTrack2),
        }));
        Assert.That(rom.Files![1].Sha1, Is.EqualTo(Convert.ToHexStringLower(SHA1.HashData(Audio))));

        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QueryAsync<string>("SELECT external_file_name FROM rom_files ORDER BY ordinal"), Is.EqualTo(new[] { SetCue, SetTrack2 }));
        Assert.That(await AuditAsync(), Is.EqualTo(new[] { (AuditActions.Ingested, (string?)$"release {SetTrack1}") }));
    }

    [Test]
    public async Task Set_NoMatch_EveryMemberGoesToReview()
    {
        var dropped = DropDisc("Mystery Disc.cue", ("Mystery Disc (Track 1).bin", RandomNumberGenerator.GetBytes(2352)), ("Mystery Disc (Track 2).bin", Audio2));

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Review, (string?)"NoDatMatch"))));
        Assert.That(IngestLeft(), Is.EqualTo(dropped.Order(StringComparer.Ordinal)));
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QueryAsync<string>("SELECT original_file_name FROM ingest_review_items ORDER BY original_file_name"),
            Is.EqualTo(dropped.Order(StringComparer.Ordinal)));
    }

    [Test]
    public async Task Set_TwoMatches_EveryMemberIsAmbiguousSet()
    {
        var dropped = DropDisc("Odd.cue", ("Odd (Track 1).bin", SetData), ("Odd (Track 2).bin", TrackData));

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Review, (string?)ReviewReasons.AmbiguousSet))));
        await using var db = Service<IDbConnectionFactory>().Create();
        var rows = (await db.QueryAsync<(string Reason, string Suggestions, string? Identification, string? Candidates)>(
            "SELECT reason, suggestions, dat_identification, candidate_game_ids FROM ingest_review_items")).ToList();
        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows, Is.All.EqualTo((ReviewReasons.AmbiguousSet, "[]", (string?)null, (string?)null)));
    }

    [Test]
    public async Task Set_ThirdMoveFails_TheFirstTwoGoBack_AndIngestIsUnchanged()
    {
        var dropped = DropSetGame();
        var hooks = new ExternalIngestHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => Path.GetFileName(to).StartsWith(SetTrack2, StringComparison.Ordinal)
                    ? new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused")
                    : NoReplaceRename.Move(from, to),
            },
        };

        await RunJobAsync(Pipeline(hooks));

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Error, (string?)IngestErrors.IoError))));
        Assert.That(IngestLeft(), Is.EqualTo(dropped.Order(StringComparer.Ordinal)));
        Assert.That(File.ReadAllBytes(Path.Combine(SetIngest, SetTrack1)), Is.EqualTo(SetData));
        Assert.That(Directory.GetFiles(SetFolder('S')), Is.Empty);
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM games"), Is.Zero);
    }

    [Test]
    public async Task Set_TargetZipUnreadable_EveryMemberGetsThatError_AndStays()
    {
        DropSetGame();
        await RunJobAsync(Pipeline());
        File.WriteAllText(Path.Combine(SetFolder('S'), "Set Game.zip"), "not a zip");
        _items.Clear();
        var disc2 = DropDisc("Set Game (USA) (Disc 2).cue", ("Set Game (USA) (Disc 2) (Track 1).bin", SetData2), ("Set Game (USA) (Disc 2) (Track 2).bin", Audio2));

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(disc2.Select(n => (n, IngestOutcome.Error, (string?)IngestErrors.TargetZipUnreadable))));
        Assert.That(IngestLeft(), Is.EqualTo(disc2.Order(StringComparer.Ordinal)));
    }
}
