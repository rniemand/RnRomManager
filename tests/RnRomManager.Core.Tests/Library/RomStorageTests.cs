using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

public class RomStorageTests
{
    private static readonly byte[] Bytes = TestLibrary.RomBytes(1);

    private static InfoJsonGame Game(string system, params string?[] pointers) =>
        TestLibrary.Game("G", [.. pointers.Select((p, i) => TestLibrary.Rom($"R{i}.nds", Bytes, isPrimary: i == 0) with { ExternalFileName = p })])
            with { System = system };

    [Test]
    public void EmbeddedHandler_IsEmbedded_NeverLegacy()
    {
        var gb = Substitute.For<ISystemHandler>();
        gb.EmbedRomsInZip.Returns(true);
        Assert.That(RomStorage.IsEmbedded(gb, Game("gb", (string?)null)), Is.True);
        Assert.That(RomStorage.IsLegacy(gb, Game("gb", (string?)null)), Is.False);
    }

    [Test]
    public void ExternalHandler_IsExternal_EvenWithoutPointers()
    {
        var ext = new ExternalTestHandler();
        Assert.That(RomStorage.IsEmbedded(ext, Game("ext", (string?)null)), Is.False);
        Assert.That(RomStorage.IsLegacy(ext, Game("ext", (string?)null)), Is.False);
    }

    [Test]
    public void LegacyHandler_AllEmbedded_IsLegacy()
    {
        var legacy = new LegacyExternalTestHandler();
        Assert.That(RomStorage.IsEmbedded(legacy, Game("legacy", null, null)), Is.True);
        Assert.That(RomStorage.IsLegacy(legacy, Game("legacy", null, null)), Is.True);
    }

    [TestCase("G.R0.nds", "G.R1.nds")]
    [TestCase("G.R0.nds", null)]
    public void LegacyHandler_AnyPointer_IsExternal(string? first, string? second)
    {
        var legacy = new LegacyExternalTestHandler();
        Assert.That(RomStorage.IsEmbedded(legacy, Game("legacy", first, second)), Is.False);
        Assert.That(RomStorage.IsLegacy(legacy, Game("legacy", first, second)), Is.False);
    }
}
