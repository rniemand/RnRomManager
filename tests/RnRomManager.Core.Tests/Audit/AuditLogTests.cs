using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Tests.Audit;

/// <summary>AU2 recording and AU1 paths.</summary>
public class AuditLogTests
{
    private static readonly AuditActor Actor = new(1, "boss", null);

    private static int Warnings(ILogger logger) =>
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);

    [Test]
    public void Record_StampsTheTime_AndQueuesWithoutWaitingForTheDatabase()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        var repository = Substitute.For<IAuditRepository>();
        var log = new AuditLog(repository, time, Substitute.For<ILogger<AuditLog>>());

        log.Record(new AuditEvent(AuditActions.Ingested, Actor, Path: "ingest/gb/a.gb"));

        Assert.That(log.Reader.TryRead(out var queued), Is.True);
        Assert.That((queued!.OccurredAt, queued.Action, queued.Path), Is.EqualTo((time.GetUtcNow().UtcDateTime, "Ingested", "ingest/gb/a.gb")));
        Assert.That(log.Pending, Is.EqualTo(1));
        Assert.That(repository.ReceivedCalls(), Is.Empty, "nothing written synchronously");
    }

    [Test]
    public void Record_SwallowsAndLogsAnInternalFailure()
    {
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(_ => throw new InvalidOperationException("clock"));
        var logger = Substitute.For<ILogger<AuditLog>>();
        var log = new AuditLog(Substitute.For<IAuditRepository>(), time, logger);

        Assert.DoesNotThrow(() => log.Record(new AuditEvent(AuditActions.Ingested, Actor)));
        Assert.That(Warnings(logger), Is.EqualTo(1));
        Assert.That(log.Pending, Is.Zero);
    }

    [Test]
    public void Record_OnAFullQueue_DropsTheEventWithAWarning()
    {
        var logger = Substitute.For<ILogger<AuditLog>>();
        var log = new AuditLog(Substitute.For<IAuditRepository>(), TimeProvider.System, logger, capacity: 2);

        for (var i = 0; i < 3; i++)
            log.Record(new AuditEvent(AuditActions.Ingested, Actor, Details: $"{i}"));

        Assert.That(log.Pending, Is.EqualTo(2));
        Assert.That(Warnings(logger), Is.EqualTo(1));
    }

    [Test]
    public async Task PurgeAsync_DelegatesToTheRepository()
    {
        var repository = Substitute.For<IAuditRepository>();
        repository.PurgeAsync(default, default).ReturnsForAnyArgs(3);
        var log = new AuditLog(repository, TimeProvider.System, Substitute.For<ILogger<AuditLog>>());
        var cutoff = new DateTime(2026, 10, 7, 0, 5, 0, DateTimeKind.Utc);

        Assert.That(await log.PurgeAsync(cutoff, default), Is.EqualTo(3));
        await repository.Received(1).PurgeAsync(cutoff, Arg.Any<CancellationToken>());
        Assert.That(AuditLog.Retention, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    [Test]
    public void AuditPaths_AreRelativeAndPrefixed_OrJustTheFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "rnrm-audit");
        var paths = new AuditPaths(Options.Create(new PathsOptions
        {
            Roms = Path.Combine(root, "roms"), Ingest = Path.Combine(root, "ingest"),
        }));

        Assert.That(paths.Of(Path.Combine(root, "roms", "gb", "T", "Tetris.zip")), Is.EqualTo("roms/gb/T/Tetris.zip"));
        Assert.That(paths.Of(Path.Combine(root, "ingest", "gb", "_failed", "x.gb")), Is.EqualTo("ingest/gb/_failed/x.gb"));
        Assert.That(paths.Of(Path.Combine(root, "cache", "work", "j", "inner.gb")), Is.EqualTo("inner.gb"));
        Assert.That(paths.Of(Path.Combine(root, "ingest-other", "a.gb")), Is.EqualTo("a.gb"), "a sibling with a shared prefix isn't inside");
        Assert.That(paths.ArchiveEntry(Path.Combine(root, "ingest", "gb", "pack.zip"), "nested/a.gb"), Is.EqualTo("ingest/gb/pack.zip › nested/a.gb"));
        Assert.That(AuditPaths.Library("gb/T/Tetris.zip"), Is.EqualTo("roms/gb/T/Tetris.zip"));
    }
}
