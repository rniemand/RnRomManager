using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Integrity;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.Core.Integrity.IntegrityIssueType;

namespace RnRomManager.Data.Tests.Database;

/// <summary>G4: <see cref="IntegrityIssueRepository.ResolveOpenAsync"/> against MariaDB.</summary>
[Category("Integration")]
public class IntegrityIssueRepositoryTests
{
    private static readonly DateTime When = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private IntegrityIssueRepository _issues = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _issues = new IntegrityIssueRepository(factory);
    }

    private Task<long> InsertAsync(
        string type,
        string zipPath,
        string? entryName = null,
        string status = IntegrityIssue.Open) =>
        _issues.InsertAsync(new IntegrityIssue
        {
            Type = type, Severity = "Info", ZipPath = zipPath, EntryName = entryName, Status = status,
            FirstSeenJobId = Guid.NewGuid(), LastSeenJobId = Guid.NewGuid(), DetectedAt = When,
        }, default);

    [Test]
    public async Task ResolveOpen_ResolvesOnlyThatZipsOpenIssueOfThatType_WithoutAnEntry_G4()
    {
        var target = await InsertAsync(DefaultBoxArt, "ext/S/Space Game.zip");
        await InsertAsync(MissingBoxArt, "ext/S/Space Game.zip");
        await InsertAsync(DefaultBoxArt, "ext/S/Space Game.zip", entryName: "boxart-01.png");
        await InsertAsync(DefaultBoxArt, "ext/O/Other Game.zip");
        var resolved = await InsertAsync(DefaultBoxArt, "ext/S/Space Game.zip", status: IntegrityIssue.Resolved);

        var count = await _issues.ResolveOpenAsync("ext/S/Space Game.zip", DefaultBoxArt, When.AddHours(1), default);

        Assert.That(count, Is.EqualTo(1));
        var all = (await _issues.ListAsync(null, null, 1, 50, default)).Items.ToDictionary(i => i.Id);
        Assert.That((all[target].Status, all[target].ResolvedAt), Is.EqualTo((IntegrityIssue.Resolved, (DateTime?)When.AddHours(1))));
        Assert.That(all.Values.Where(i => i.Id != target && i.Id != resolved).Select(i => i.Status), Is.All.EqualTo(IntegrityIssue.Open));
        Assert.That(all[resolved].ResolvedAt, Is.Null, "an already-resolved row is left alone");
    }
}
