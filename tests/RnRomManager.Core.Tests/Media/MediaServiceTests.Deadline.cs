using System.Net;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Media;

/// <summary>Spec T8: an external system's image body has its own 20 s deadline, on the fake clock; only a finished body resets the breaker.</summary>
public partial class MediaServiceTests
{
    /// <summary>A 200 whose body never arrives: <see cref="Reading"/> completes once the body is being read.</summary>
    private sealed class StalledBody : Stream
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reading => _reading.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Serves a stalled body at <paramref name="url"/>; the returned list gets each body as it is requested.</summary>
    private List<StalledBody> Stall(string url)
    {
        var bodies = new List<StalledBody>();
        _routes[url] = () =>
        {
            var body = new StalledBody();
            lock (bodies)
                bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        };
        return bodies;
    }

    /// <summary>Waits until the first stalled body of <paramref name="bodies"/> is being read.</summary>
    private static async Task ReadingAsync(List<StalledBody> bodies)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            StalledBody? body;
            lock (bodies)
                body = bodies.FirstOrDefault();
            if (body is not null)
            {
                await body.Reading.WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }
            if (DateTime.UtcNow > deadline)
                Assert.Fail("the stalled body was never requested");
            await Task.Delay(10);
        }
    }

    /// <summary>Lets <paramref name="bodies"/> start, then moves the fake clock to the deadline.</summary>
    private async Task ExpireAsync(List<StalledBody> bodies)
    {
        await ReadingAsync(bodies);
        _clock.Advance(MediaService.BodyTimeout);
    }

    [Test]
    public async Task StalledBody_HitsTheDeadline_CountsOnce_AndFallsBack()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "stall.png"), Entry("0100000000004000", Cdn + "next.png"));
        var bodies = Stall(Cdn + "stall.png");

        var finding = FindAsync(GameName("0100000000002000"), GameName("0100000000004000"));
        await ExpireAsync(bodies);
        var result = await finding.WaitAsync(TimeSpan.FromSeconds(10));

        AssertDefault(result.BoxArt);
        Assert.That(_server.Requests.Select(r => r.RequestUri!.AbsoluteUri), Is.EqualTo(new[] { Cdn + "stall.png", Cdn + "next.png" }),
            "the item goes on to the next candidate, and the stalled body isn't requested again");
        Assert.That((_breaker.RecordFailure(), _breaker.RecordFailure()), Is.EqualTo((false, true)), "exactly one failure was counted");
    }

    [Test]
    public async Task ThreeStalledBodies_OpenTheBreaker_AndSuppressAFourthRequest()
    {
        var ids = new[] { "0100000000002000", "0100000000004000", "0100000000006000", "0100000000008000" };
        UseTitleDb([.. ids.Select(id => Entry(id, Cdn + id + ".png"))]);
        var bodies = ids.Select(id => Stall(Cdn + id + ".png")).ToArray();

        var finding = FindAsync([.. ids.Select(GameName)]);
        for (var i = 0; i < 3; i++)
            await ExpireAsync(bodies[i]);
        var result = await finding.WaitAsync(TimeSpan.FromSeconds(10));

        AssertDefault(result.BoxArt);
        Assert.That(_breaker.IsOpen, Is.True);
        Assert.That(_server.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task OversizedBody_AfterTwoFailures_LeavesTheStreakAtTwo()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "a.png"), Entry("0100000000004000", Cdn + "b.png"), Entry("0100000000006000", Cdn + "big.png"));
        _routes[Cdn + "a.png"] = () => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        _routes[Cdn + "b.png"] = () => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        _routes[Cdn + "big.png"] = () => StubHttpHandler.Bytes(new byte[MediaService.MaxThumbnailBytes + 1]);

        var result = await FindAsync(GameName("0100000000002000"), GameName("0100000000004000"), GameName("0100000000006000"));

        AssertDefault(result.BoxArt);
        Assert.That(_breaker.RecordFailure(), Is.True, "neither the 200 header nor the capped-out body reset it");
    }

    [Test]
    public async Task CompletedBody_ResetsTheStreak_EvenIfItsFormatIsRefused()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "a.png"), Entry("0100000000004000", Cdn + "b.png"), Entry("0100000000006000", Cdn + "junk.png"));
        _routes[Cdn + "a.png"] = () => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        _routes[Cdn + "b.png"] = () => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        _routes[Cdn + "junk.png"] = () => StubHttpHandler.Bytes([1, 2, 3]);

        await FindAsync(GameName("0100000000002000"), GameName("0100000000004000"), GameName("0100000000006000"));

        Assert.That((_breaker.RecordFailure(), _breaker.RecordFailure()), Is.EqualTo((false, false)), "back to zero");
    }

    [Test]
    public async Task ACancelledCaller_StopsTheBodyRead()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "stall.png"));
        var bodies = Stall(Cdn + "stall.png");
        using var cts = new CancellationTokenSource();

        var finding = Service().FindAsync(_handler, [GameName("0100000000002000")], _breaker, cts.Token);
        await ReadingAsync(bodies);
        cts.Cancel();

        Assert.That(async () => await finding.WaitAsync(TimeSpan.FromSeconds(10)), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(_breaker.RecordFailure(), Is.False, "a cancel isn't a failure");
    }

    [Test]
    public async Task Embedded_A200HeaderStillResetsTheStreak()
    {
        _routes[GbBox + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(new byte[MediaService.MaxThumbnailBytes + 1]);
        _breaker.RecordFailure();
        _breaker.RecordFailure();

        await FindAsync("Tetris (World)");

        Assert.That(_breaker.RecordFailure(), Is.False, "libretro's existing rule: the 200 resets before the body");
    }
}
