using RnRomManager.Core.Discovery;

namespace RnRomManager.Core.Tests.Discovery;

public class DiscoveryKeyTests
{
    [TestCase("c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516", true)]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", true)]
    [TestCase("C0A4942143E872CD1AE29FC759E04526DE2E909AC1732734D38550A29C2E2516", false)]
    [TestCase("c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e251", false)]
    [TestCase("c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e25160", false)]
    [TestCase("g0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516", false)]
    [TestCase("c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e251 ", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValid_AcceptsOnly64LowercaseHexCharacters(string? value, bool valid)
    {
        Assert.That(DiscoveryKey.IsValid(value), Is.EqualTo(valid));
    }

    [TestCase("nes")]
    [TestCase("NES")]
    public void ForSystem_IsTheSha256OfTheLowercaseId(string systemId)
    {
        var key = DiscoveryKey.ForSystem(systemId);

        Assert.That(key, Is.EqualTo("7accb4417aea998fa57c118399884150b6daa597addcc83a3ec4ff9a1c4f5c1c"));
        Assert.That(DiscoveryKey.IsValid(key), Is.True);
    }

    [TestCase("publishers", DiscoveryKind.Publishers)]
    [TestCase("genres", DiscoveryKind.Genres)]
    public void TryParse_KnownKind_RoundTripsItsName(string name, DiscoveryKind expected)
    {
        Assert.That(DiscoveryKinds.TryParse(name, out var kind), Is.True);
        Assert.That(kind, Is.EqualTo(expected));
        Assert.That(kind.Name(), Is.EqualTo(name));
    }

    [TestCase("Publishers")]
    [TestCase("publisher")]
    [TestCase("developers")]
    [TestCase(" genres")]
    [TestCase("")]
    [TestCase(null)]
    public void TryParse_AnythingElse_IsFalse(string? name)
    {
        Assert.That(DiscoveryKinds.TryParse(name, out _), Is.False);
    }
}
