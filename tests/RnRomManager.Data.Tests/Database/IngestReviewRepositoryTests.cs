using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Ingest;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

[Category("Integration")]
public class IngestReviewRepositoryTests
{
    private IngestReviewRepository _reviews = null!;
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _reviews = new IngestReviewRepository(factory);
    }

    private static ReviewItem Item(char hash, string entry) => new()
    {
        SystemId = "gb", Reason = "AmbiguousGroup", SourcePath = "/ingest/gb/pack.zip", ArchivePath = "/ingest/gb/pack.zip",
        ArchiveEntry = entry, OriginalFileName = entry, SizeBytes = 32768, Sha1 = new string(hash, 40),
        Crc32 = "12345678", Md5 = new string('a', 32), CreatedAt = Now, LastSeenJobId = Guid.NewGuid(),
        Header = "{\"title\":\"TEST\"}", DatIdentification = "{\"datName\":\"GB\"}", CandidateGameIds = "[]",
    };

    [Test]
    public async Task RequeueReplacesIdentityAndPaths_PreservesCreatedAtAndId()
    {
        var item = Item('a', "old.gb");
        await _reviews.UpsertAsync(item, default);
        var before = (await _reviews.GetBySha1Async(item.Sha1, default))!;
        await _reviews.CompleteSourceAsync(item.SourcePath, item.ArchiveEntry, new IngestItemResult("Ingested", GameId: Guid.NewGuid()), Guid.NewGuid(), Now.AddMinutes(1), default);
        var replacement = item with { SystemId = "gbc", Reason = "NoDatMatch", SourcePath = "/ingest/gb/new.gbc",
            ArchivePath = null, ArchiveEntry = null, OriginalFileName = "new.gbc", Header = null,
            DatIdentification = null, CandidateGameIds = null, Suggestions = "[{\"datGameName\":\"Test\",\"romName\":\"Test.gbc\",\"score\":0.8}]",
            CreatedAt = Now.AddHours(1), LastSeenJobId = Guid.NewGuid() };
        await _reviews.UpsertAsync(replacement, default);
        var after = (await _reviews.GetBySha1Async(item.Sha1, default))!;
        Assert.That(after, Is.EqualTo(replacement with { Id = before.Id, CreatedAt = before.CreatedAt }));
        Assert.That(await _reviews.ListPendingSourcesAsync(default), Is.EqualTo(new[] { new PendingReviewSource(before.Id, replacement.SourcePath, null) }));
    }

    [Test]
    public async Task CompletingArchiveEntry_UsesExactPathAndEntry_PreservesOtherRows()
    {
        var lower = Item('a', "test.gb");
        var upper = Item('b', "TEST.gb");
        var otherPath = Item('c', "test.gb") with { SourcePath = "/ingest/gb/PACK.zip", ArchivePath = "/ingest/gb/PACK.zip" };
        foreach (var item in new[] { lower, upper, otherPath })
            await _reviews.UpsertAsync(item, default);
        await _reviews.CompleteSourceAsync(lower.SourcePath, lower.ArchiveEntry, new IngestItemResult("Error"), Guid.NewGuid(), Now, default);
        Assert.That(await _reviews.GetBySha1Async(lower.Sha1, default), Is.Null);
        Assert.That(await _reviews.GetBySha1Async(upper.Sha1, default), Is.Not.Null);
        Assert.That(await _reviews.GetBySha1Async(otherPath.Sha1, default), Is.Not.Null);
        var remaining = await _reviews.ListPendingSourcesAsync(default);
        await _reviews.DeleteAsync([remaining[0].Id], default);
        Assert.That(await _reviews.ListPendingSourcesAsync(default), Has.Count.EqualTo(1));
    }

    [TestCase("Skipped")]
    [TestCase("Review")]
    public async Task NonfinalOutcomes_PreservePendingRow(string outcome)
    {
        var item = Item('a', "test.gb");
        await _reviews.UpsertAsync(item, default);
        await _reviews.CompleteSourceAsync(item.SourcePath, item.ArchiveEntry, new IngestItemResult(outcome), Guid.NewGuid(), Now, default);
        Assert.That((await _reviews.GetBySha1Async(item.Sha1, default))!.Status, Is.EqualTo("Pending"));
    }
}
