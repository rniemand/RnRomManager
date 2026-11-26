using NSubstitute;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Systems;

public class SystemRegistryTests
{
    private static ISystemHandler Handler(string id, string family, params string[] extensions)
    {
        var handler = TestHandlers.Fake(id);
        Embed(handler, true);
        handler.Family.Returns(family);
        handler.RomExtensions.Returns(extensions);
        return handler;
    }

    /// <summary>The registry checks <c>DefaultEmbedRomsInZip</c>, which a substitute doesn't derive from <c>EmbedRomsInZip</c>.</summary>
    private static void Embed(ISystemHandler handler, bool embed)
    {
        handler.EmbedRomsInZip.Returns(embed);
        handler.DefaultEmbedRomsInZip.Returns(embed);
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
        Assert.That(Registry().ByFamily("md"), Is.Empty);
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
    public void MixedStorageFamily_Throws_NamingTheFamily()
    {
        var external = Handler("gbx", "gameboy", ".gbx");
        Embed(external, false);

        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([_gb, _gba, external]));

        Assert.That(ex!.Message, Does.Contain("'gameboy'"));
    }

    [Test]
    public void ExternalFamily_ThatAgrees_IsAccepted()
    {
        var a = Handler("xa", "ext", ".xa");
        var b = Handler("xb", "ext", ".xb");
        Embed(a, false);
        Embed(b, false);

        Assert.That(new SystemRegistry([_gb, a, b]).ByFamily("ext"), Is.EqualTo(new[] { a, b }));
    }

    [Test]
    public void FileSets_WithEmbeddedStorage_Throws_NamingTheSystem()
    {
        var disc = Handler("disc", "disc", ".bin");
        disc.FileSets.Returns(true);

        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([_gb, disc]));

        Assert.That(ex!.Message, Does.Contain("'disc'").And.Contain("FileSets"));
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void LegacyEmbeddedRoms_OnlyOnExternalWithoutFileSets(bool embedded, bool fileSets)
    {
        var legacy = Handler("nds", "nds", ".nds");
        Embed(legacy, embedded);
        legacy.FileSets.Returns(fileSets);
        legacy.LegacyEmbeddedRoms.Returns(true);

        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([_gb, legacy]));

        Assert.That(ex!.Message, Does.Contain("'nds'").And.Contain("LegacyEmbeddedRoms"));
    }

    [Test]
    public void LegacyEmbeddedRoms_OnExternal_IsAccepted()
    {
        var legacy = Handler("nds", "nds", ".nds");
        Embed(legacy, false);
        legacy.LegacyEmbeddedRoms.Returns(true);

        Assert.That(new SystemRegistry([_gb, legacy]).ById("nds"), Is.SameAs(legacy));
    }

    [Test]
    public void FamilyMixingFileSets_Throws_NamingTheFamily()
    {
        var a = Handler("xa", "disc", ".xa");
        var b = Handler("xb", "disc", ".xb");
        Embed(a, false);
        Embed(b, false);
        a.FileSets.Returns(true);

        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([a, b]));

        Assert.That(ex!.Message, Does.Contain("'disc'").And.Contain("FileSets"));
    }

    [Test]
    public void DuplicateId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([_gb, Handler("gb", "other", ".x")]));

        Assert.That(ex!.Message, Does.Contain("'gb'"));
    }

    [Test]
    public void Rules_UseTheDefault_NotTheCurrentSetting()
    {
        var legacy = Handler("nds", "nds", ".nds");
        legacy.EmbedRomsInZip.Returns(true);
        legacy.DefaultEmbedRomsInZip.Returns(false);
        legacy.LegacyEmbeddedRoms.Returns(true);
        legacy.RomStorageConfigurable.Returns(true);

        Assert.That(new SystemRegistry([_gb, legacy]).ById("nds"), Is.SameAs(legacy));
    }

    [Test]
    public void RomStorageConfigurable_WithoutLegacyEmbeddedRoms_Throws()
    {
        var plain = Handler("xa", "xa", ".xa");
        plain.RomStorageConfigurable.Returns(true);

        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([_gb, plain]));

        Assert.That(ex!.Message, Does.Contain("'xa'").And.Contain("RomStorageConfigurable"));
    }

    [Test]
    public void RomStorageConfigurable_SharingItsFamily_Throws()
    {
        var a = Handler("xa", "ext", ".xa");
        var b = Handler("xb", "ext", ".xb");
        foreach (var h in new[] { a, b })
        {
            Embed(h, false);
            h.LegacyEmbeddedRoms.Returns(true);
        }
        a.RomStorageConfigurable.Returns(true);

        var ex = Assert.Throws<InvalidOperationException>(() => new SystemRegistry([a, b]));

        Assert.That(ex!.Message, Does.Contain("'xa'").And.Contain("RomStorageConfigurable"));
    }
}
