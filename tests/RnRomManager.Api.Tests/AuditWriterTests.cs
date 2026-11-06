using System.Security.Claims;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;

namespace RnRomManager.Api.Tests;

/// <summary>AU2 writer and AU3 request actor.</summary>
public class AuditWriterTests
{
    private static readonly AuditActor Actor = new(1, "boss", null);

    [Test]
    public async Task Writes_EveryEvent_LogsAndDropsAFailedInsert_AndKeepsGoing()
    {
        var repository = Substitute.For<IAuditRepository>();
        var written = new List<string>();
        repository.InsertAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var e = call.Arg<AuditEvent>();
            if (e.Details == "bad")
                throw new InvalidOperationException("db down");
            written.Add(e.Details!);
            return Task.CompletedTask;
        });
        var writerLogger = Substitute.For<ILogger<AuditWriter>>();
        var log = new AuditLog(repository, TimeProvider.System, Substitute.For<ILogger<AuditLog>>());
        using var writer = new AuditWriter(log, repository, writerLogger);
        await writer.StartAsync(CancellationToken.None);

        foreach (var details in new[] { "one", "bad", "two" })
            log.Record(new AuditEvent(AuditActions.Ingested, Actor, Details: details));
        await writer.FlushAsync();

        Assert.That(written, Is.EqualTo(new[] { "one", "two" }));
        Assert.That(log.Pending, Is.Zero);
        Assert.That(writerLogger.ReceivedCalls().Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Warning), Is.EqualTo(1));
        await writer.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Inserts_UseCancellationTokenNone_AndStopDrainsTheQueue()
    {
        var repository = Substitute.For<IAuditRepository>();
        var log = new AuditLog(repository, TimeProvider.System, Substitute.For<ILogger<AuditLog>>());
        using var writer = new AuditWriter(log, repository, Substitute.For<ILogger<AuditWriter>>());

        // Recorded before the writer ever runs: stopping still writes them.
        log.Record(new AuditEvent(AuditActions.Ingested, Actor));
        log.Record(new AuditEvent(AuditActions.MovedToFailed, Actor));
        await writer.StopAsync(CancellationToken.None);

        await repository.Received(2).InsertAsync(Arg.Any<AuditEvent>(), CancellationToken.None);
        Assert.That(log.Pending, Is.Zero);
    }

    [Test]
    public void FlushAsync_TimesOutWhenNothingDrains()
    {
        var repository = Substitute.For<IAuditRepository>();
        var log = new AuditLog(repository, TimeProvider.System, Substitute.For<ILogger<AuditLog>>());
        using var writer = new AuditWriter(log, repository, Substitute.For<ILogger<AuditWriter>>());
        log.Record(new AuditEvent(AuditActions.Ingested, Actor));

        Assert.ThrowsAsync<TimeoutException>(() => writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Test]
    public void AuditActorFrom_ReadsTheUserIdAndName()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Name, "boss")], "cookie"));

        Assert.That(AuditActor.From(user), Is.EqualTo(new AuditActor(42, "boss", null)));
        Assert.That(AuditActor.From(new ClaimsPrincipal()), Is.EqualTo(new AuditActor(null, null, null)));
    }
}
