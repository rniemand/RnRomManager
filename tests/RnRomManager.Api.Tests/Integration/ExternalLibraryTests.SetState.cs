using System.Security.Cryptography;
using Dapper;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B7a: a set against what the library and Review already hold.</summary>
public partial class ExternalLibraryTests
{
    private static readonly byte[] SharedAudio = RandomNumberGenerator.GetBytes(2352 * 2 + 4);

    private string SetDuplicates(string name) => Path.Combine(SetIngest, "_duplicates", name);

    private async Task SqlAsync(string sql, object? args = null)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        await db.ExecuteAsync(sql, args);
    }

    /// <summary>A Pending review row for <paramref name="bytes"/> whose source is another, still present file.</summary>
    private async Task<string> PendingElsewhereAsync(byte[] bytes)
    {
        var hold = Path.Combine(SetIngest, "_hold");
        Directory.CreateDirectory(hold);
        var path = Path.Combine(hold, "elsewhere.bin");
        File.WriteAllBytes(path, bytes);
        await Service<IIngestReviewRepository>().UpsertAsync(new ReviewItem
        {
            SystemId = ExternalSetTestHandler.SystemId, Reason = "NoDatMatch", SourcePath = path, OriginalFileName = "elsewhere.bin",
            SizeBytes = bytes.Length, Crc32 = Crc32.ToHex(bytes), Md5 = Convert.ToHexStringLower(MD5.HashData(bytes)),
            Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), CreatedAt = DateTime.UtcNow, LastSeenJobId = Guid.NewGuid(),
        }, CancellationToken.None);
        return path;
    }

    [Test]
    public async Task Set_DuplicateDataTrack_SendsEveryMemberToDuplicates()
    {
        DropSetGame();
        await RunJobAsync(Pipeline());
        _items.Clear();
        var again = DropSetGame();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(again.Select(n => (n, IngestOutcome.Duplicate, (string?)null))));
        Assert.That(IngestLeft(), Is.Empty);
        Assert.That(again.Select(n => File.Exists(SetDuplicates(n))), Is.All.True);
        Assert.That((await AuditAsync()).Count(a => a.Action == AuditActions.MovedToDuplicates), Is.EqualTo(3));
    }

    [Test]
    public async Task Set_AmbiguousGroup_SkipsEveryMember_AndALoneBinToo()
    {
        await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso));
        DropDisc("Set Game (USA) (Disc 2).cue", ("Set Game (USA) (Disc 2) (Track 1).bin", SetData2), ("Set Game (USA) (Disc 2) (Track 2).bin", Audio2));
        await RunJobAsync(Pipeline());
        // Two games now own "Set Game", and both own "Single Game".
        await SqlAsync("UPDATE roms SET base_title = 'Set Game' WHERE base_title = 'Disc Game'");
        _items.Clear();
        var dropped = DropSetGame();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Skipped, (string?)IngestReason.AmbiguousGroup))));
        Assert.That(IngestLeft(), Is.EqualTo(dropped.Order(StringComparer.Ordinal)));

        await SqlAsync("UPDATE roms SET base_title = 'Single Game'");
        var lone = await IngestAsync(Pipeline(), DropSet("single.bin", SingleBin));
        Assert.That(Code(lone), Is.EqualTo((IngestOutcome.Skipped, (string?)IngestReason.AmbiguousGroup)));
        Assert.That(File.Exists(Path.Combine(SetIngest, "single.bin")), Is.True);
    }

    [Test]
    public async Task Set_DataTrackPendingFromAnotherSource_SendsEveryMemberToDuplicates()
    {
        await PendingElsewhereAsync(SetData);
        var dropped = DropSetGame();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Duplicate, (string?)null))));
        Assert.That(dropped.Select(n => File.Exists(SetDuplicates(n))), Is.All.True);
    }

    [Test]
    public async Task Set_DismissedDataTrack_IsIngested()
    {
        await PendingElsewhereAsync(SetData);
        await SqlAsync("UPDATE ingest_review_items SET status = 'Dismissed'");
        var dropped = DropSetGame();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Ingested, (string?)null))));
    }

    [Test]
    public async Task TwoUnmatchedSets_SharingAnAudioTrack_QueueItOnce()
    {
        DropDisc("Alpha.cue", ("Alpha (Track 1).bin", RandomNumberGenerator.GetBytes(2352)), ("Alpha (Track 2).bin", SharedAudio));
        DropDisc("Beta.cue", ("Beta (Track 1).bin", RandomNumberGenerator.GetBytes(2352)), ("Beta (Track 2).bin", SharedAudio));

        await RunJobAsync(Pipeline());

        Assert.That(SetItems().Where(i => i.Name.Contains("Track 2")), Is.EqualTo(new[]
        {
            ("Alpha (Track 2).bin", IngestOutcome.Review, (string?)"NoDatMatch"),
            ("Beta (Track 2).bin", IngestOutcome.Skipped, IngestReason.AlreadyInReview),
        }));
        Assert.That(File.Exists(Path.Combine(SetIngest, "Beta (Track 2).bin")), Is.True, "it stays with its set");
    }

    [Test]
    public async Task UnmatchedSet_RerunKeepsItsItemsInReview_ThenIngestsOnceTheDatListsIt()
    {
        var late = RandomNumberGenerator.GetBytes(2352 * 2);
        var dropped = DropDisc("Late Game (USA).cue", ("Late Game (USA) (Track 1).bin", late), ("Late Game (USA) (Track 2).bin", Audio2));
        await RunJobAsync(Pipeline());
        _items.Clear();

        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Review, (string?)"NoDatMatch"))), "own rows are upserted");

        WriteDats(new FixtureGame("Late Game (USA)", "Late Game (USA) (Track 1).bin", late));
        _items.Clear();
        await RunJobAsync(Pipeline());

        Assert.That(SetItems(), Is.EqualTo(dropped.Select(n => (n, IngestOutcome.Ingested, (string?)null))));
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QueryAsync<string>("SELECT status FROM ingest_review_items"), Is.EqualTo(new[] { "Resolved", "Resolved", "Resolved" }));
    }
}
