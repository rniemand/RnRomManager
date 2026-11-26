using Microsoft.Extensions.Time.Testing;
using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>Bundle Creator B7: the ticket stream's measured copy and its two-minute inactivity timeout, on a fake clock.</summary>
public class BundleDownloadStreamTests
{
    private static readonly byte[] Bytes = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
    private static readonly TimeSpan Timeout = BundleDownloadEndpoints.InactivityTimeout;

    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp() => _clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    [Test]
    public async Task CopiesExactlyTheLength_AndNeverReadsPastIt()
    {
        var target = new MemoryStream();
        var source = new CountingStream(new MemoryStream(Bytes));

        await BundleDownloadEndpoints.CopyAsync(source, target, 200_000, Timeout, _clock, default);

        Assert.That(target.ToArray(), Is.EqualTo(Bytes[..200_000]));
        Assert.That(source.BytesRead, Is.EqualTo(200_000));
    }

    [Test]
    public void ASourceThatEndsEarly_IsAnIOException()
    {
        var target = new MemoryStream();

        Assert.ThrowsAsync<IOException>(() => BundleDownloadEndpoints.CopyAsync(new MemoryStream(Bytes[..1000]), target, 2000, Timeout, _clock, default));
        Assert.That(target.Length, Is.EqualTo(1000));
    }

    [Test]
    public void AnAbortedRequest_IsACancellation()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() =>
            BundleDownloadEndpoints.CopyAsync(new MemoryStream(Bytes), new MemoryStream(), Bytes.Length, Timeout, _clock, aborted.Token));
    }

    [Test]
    public async Task AStalledRead_TimesOutAfterTwoMinutes()
    {
        var source = new StallingStream(stallReads: true);
        var copy = BundleDownloadEndpoints.CopyAsync(source, new MemoryStream(), 10, Timeout, _clock, default);
        await source.Stalled.Task;

        _clock.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.That(copy.IsCompleted, Is.False);
        _clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => copy);
    }

    [Test]
    public async Task AStalledWrite_TimesOutAfterTwoMinutes()
    {
        var target = new StallingStream(stallReads: false);
        var copy = BundleDownloadEndpoints.CopyAsync(new MemoryStream(Bytes), target, Bytes.Length, Timeout, _clock, default);
        await target.Stalled.Task;

        _clock.Advance(Timeout);

        await Assert.ThrowsAsync<TimeoutException>(() => copy);
    }

    [Test]
    public async Task SteadyProgress_NeverTimesOut_HoweverLongTheWholeCopy()
    {
        var source = new SlowStream(new MemoryStream(Bytes), _clock, TimeSpan.FromSeconds(90));

        await BundleDownloadEndpoints.CopyAsync(source, new MemoryStream(), Bytes.Length, Timeout, _clock, default);

        Assert.That(_clock.GetUtcNow() - new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), Is.GreaterThan(Timeout));
    }
}
