using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Tests.Storage;

public class LimitStreamTests
{
    private static MemoryStream Bytes(int count) => new(Enumerable.Range(0, count).Select(i => (byte)i).ToArray());

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(100_000)]
    public void ExactlyTheLimit_ReadsEverything(int size)
    {
        using var stream = new LimitStream(Bytes(size), size);
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        Assert.That(copy.Length, Is.EqualTo(size));
        Assert.That(stream.BytesRead, Is.EqualTo(size));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(100_000)]
    public void OneByteOver_Throws(int limit)
    {
        using var stream = new LimitStream(Bytes(limit + 1), limit);

        var ex = Assert.Throws<LimitExceededException>(() => stream.CopyTo(Stream.Null));

        Assert.That(ex!.Limit, Is.EqualTo(limit));
    }

    [Test]
    public async Task OverTheLimit_ThrowsOnTheAsyncPathToo()
    {
        await using var stream = new LimitStream(Bytes(10), 9);

        await Assert.ThrowsAsync<LimitExceededException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Test]
    public void SmallReads_CountTowardTheLimit()
    {
        using var stream = new LimitStream(Bytes(5), 3);
        var buffer = new byte[1];

        for (var i = 0; i < 3; i++)
            Assert.That(stream.Read(buffer, 0, 1), Is.EqualTo(1));

        Assert.Throws<LimitExceededException>(() => stream.ReadExactly(buffer));
    }

    [Test]
    public void BigBuffer_NeverPullsMoreThanOneBytePastTheLimit()
    {
        var inner = Bytes(1000);
        using var stream = new LimitStream(inner, 10);

        Assert.Throws<LimitExceededException>(() => stream.ReadExactly(new byte[1000]));
        Assert.That(inner.Position, Is.EqualTo(11));
    }

    [Test]
    public void HugeLimit_DoesNotOverflow()
    {
        using var stream = new LimitStream(Bytes(10), long.MaxValue);

        stream.CopyTo(Stream.Null);

        Assert.That(stream.BytesRead, Is.EqualTo(10));
    }

    [Test]
    public void Dispose_DisposesTheInnerStream()
    {
        var inner = Bytes(1);
        new LimitStream(inner, 1).Dispose();

        Assert.That(inner.CanRead, Is.False);
    }
}
