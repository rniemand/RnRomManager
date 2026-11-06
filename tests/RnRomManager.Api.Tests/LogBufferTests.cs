using Microsoft.Extensions.Time.Testing;
using RnRomManager.Api.Logging;

namespace RnRomManager.Api.Tests;

public class LogBufferTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly long FirstId = Start.ToUnixTimeMilliseconds() * 1000;

    private static LogBuffer NewBuffer(int capacity = LogBuffer.DefaultCapacity) => new(new FakeTimeProvider(Start), capacity);

    private static void AddMany(LogBuffer buffer, int count)
    {
        for (var i = 0; i < count; i++)
            buffer.Add(Start.UtcDateTime, "Info", "RnRomManager.Test", $"m{i}", null);
    }

    [Test]
    public void IdsStartAtTheCreationTimeAndIncrease()
    {
        var buffer = NewBuffer();
        AddMany(buffer, 3);

        Assert.That(buffer.Since(null).Select(e => e.Id), Is.EqualTo(new[] { FirstId, FirstId + 1, FirstId + 2 }));
    }

    [TestCase(30, 20, "m10", "m29")]
    [TestCase(5, 5, "m0", "m4")]
    public void Recent_IsTheLastCountOldestFirst(int added, int expected, string first, string last)
    {
        var buffer = NewBuffer();
        AddMany(buffer, added);

        var recent = buffer.Recent(20);
        Assert.That(recent, Has.Count.EqualTo(expected));
        Assert.That((recent[0].Message, recent[^1].Message), Is.EqualTo((first, last)));
    }

    [Test]
    public void Recent_IsEmptyForAnEmptyBuffer() => Assert.That(NewBuffer().Recent(20), Is.Empty);

    [Test]
    public void KeepsTheLast500InOrder()
    {
        var buffer = NewBuffer();
        AddMany(buffer, 510);

        var events = buffer.Since(null);
        Assert.That(events, Has.Count.EqualTo(500));
        Assert.That((events[0].Message, events[^1].Message), Is.EqualTo(("m10", "m509")));
        Assert.That(events.Select(e => e.Id), Is.Ordered.Ascending);
    }

    [Test]
    public void Since_ReturnsOnlyNewer_OrEverythingForAnIdAboveTheNewest()
    {
        var buffer = NewBuffer();
        AddMany(buffer, 5);

        Assert.That(buffer.Since(FirstId + 2).Select(e => e.Message), Is.EqualTo(new[] { "m3", "m4" }));
        Assert.That(buffer.Since(FirstId + 4), Is.Empty);
        Assert.That(buffer.Since(FirstId + 5), Has.Count.EqualTo(5), "an id from another process or buffer resends all");
        Assert.That(buffer.Since(0), Has.Count.EqualTo(5));
    }

    [Test]
    public void Subscribe_GetsTheBacklogThenNewEvents_UntilDisposed()
    {
        var buffer = NewBuffer();
        AddMany(buffer, 2);

        var subscription = buffer.Subscribe(FirstId);
        buffer.Add(Start.UtcDateTime, "Warn", "L", "later", "boom");

        Assert.That(subscription.Backlog.Select(e => e.Message), Is.EqualTo(new[] { "m1" }));
        Assert.That(subscription.Events.TryRead(out var later), Is.True);
        Assert.That(later, Is.EqualTo(new LogEntry(FirstId + 2, Start.UtcDateTime, "Warn", "L", "later", "boom")));

        subscription.Dispose();
        buffer.Add(Start.UtcDateTime, "Info", "L", "after", null);
        Assert.That(subscription.Events.TryRead(out _), Is.False);
        Assert.That(subscription.Events.Completion.IsCompleted, Is.True);
    }
}
