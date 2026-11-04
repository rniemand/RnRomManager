using NSubstitute;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Tests.Systems;

public class SystemRegistryTests
{
    private static ISystemHandler Handler(string id, string family, params string[] extensions)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns(id);
        handler.Family.Returns(family);
        handler.RomExtensions.Returns(extensions);
        return handler;
    }

    private readonly ISystemHandler _gb = Handler("gb", "gameboy", ".gb");
    private readonly ISystemHandler _gbc = Handler("gbc", "gameboy", ".gbc");
    private readonly ISystemHandler _gba = Handler("gba", "gba", ".gba", ".agb");

    private SystemRegistry Registry() => new([_gb, _gbc, _gba]);

    [Test]
    public void All_KeepsRegistrationOrder() =>
        Assert.That(Registry().All, Is.EqualTo(new[] { _gb, _gbc, _gba }));

    [Test]
    public void ById_IsExact()
    {
        Assert.That(Registry().ById("gbc"), Is.SameAs(_gbc));
        Assert.That(Registry().ById("GBC"), Is.Null);
        Assert.That(Registry().ById("nes"), Is.Null);
    }

    [Test]
    public void ByFamily_ReturnsEveryMember()
    {
        Assert.That(Registry().ByFamily("gameboy"), Is.EqualTo(new[] { _gb, _gbc }));
        Assert.That(Registry().ByFamily("snes"), Is.Empty);
    }

    [TestCase(".gb", "gb")]
    [TestCase(".GB", "gb")]
    [TestCase(".Agb", "gba")]
    public void ByExtension_IgnoresCase(string extension, string expected) =>
        Assert.That(Registry().ByExtension(extension).Select(h => h.Id), Is.EqualTo(new[] { expected }));

    [Test]
    public void ByExtension_Unknown_IsEmpty() =>
        Assert.That(Registry().ByExtension(".zip"), Is.Empty);

    [Test]
    public void DuplicateId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([_gb, Handler("gb", "other", ".x")]));

        Assert.That(ex!.Message, Does.Contain("'gb'"));
    }
}
