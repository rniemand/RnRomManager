using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>Spec §13 log retention: <c>rnrommanager-*.log</c> older than <c>Logging:RetentionDays</c> are deleted (S37).</summary>
public class LogRetentionTests
{
    private TempDir _dir = null!;
    private FakeTimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 5, 0, TimeSpan.Zero));
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private LogRetention Retention(int days = 14) => new(Options.Create(new PathsOptions { Logs = _dir.Path }),
        Options.Create(new LoggingOptions { RetentionDays = days }), _time, NullLogger<LogRetention>.Instance);

    private string Log(string name)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Test]
    public void DeletesDaysBeforeTheRetentionWindow_ByTheDateInTheName()
    {
        var today = Log("rnrommanager-2026-11-20.log");
        var edge = Log("rnrommanager-2026-11-06.log");    // 14 days ago: kept
        var old = Log("rnrommanager-2026-11-05.log");     // 15 days ago
        var older = Log("rnrommanager-2025-01-01.log");
        var other = Log("something-else-2020-01-01.log");

        var deleted = Retention().Purge();

        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(new[] { today, edge, other }.All(File.Exists), Is.True);
        Assert.That(new[] { old, older }.Any(File.Exists), Is.False);
    }

    [Test]
    public void UndatedName_FallsBackToTheFileAge()
    {
        var fresh = Log("rnrommanager-manual.log");
        File.SetLastWriteTimeUtc(fresh, _time.GetUtcNow().UtcDateTime.AddDays(-3));
        var stale = Log("rnrommanager-copy.log");
        File.SetLastWriteTimeUtc(stale, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Retention().Purge();

        Assert.That((File.Exists(fresh), File.Exists(stale)), Is.EqualTo((true, false)));
    }

    [Test]
    public void RetentionDays_IsConfigurable_AndAMissingFolderIsFine()
    {
        var yesterday = Log("rnrommanager-2026-11-19.log");
        var twoDays = Log("rnrommanager-2026-11-18.log");

        Assert.That(Retention(days: 1).Purge(), Is.EqualTo(1));
        Assert.That((File.Exists(yesterday), File.Exists(twoDays)), Is.EqualTo((true, false)));

        Directory.Delete(_dir.Path, recursive: true);
        Assert.That(Retention().Purge(), Is.Zero);
    }
}
