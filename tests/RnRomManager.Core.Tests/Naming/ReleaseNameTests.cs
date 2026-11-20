using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Naming;

public class ReleaseNameTests
{
    [TestCase("Orbit Expedition - Star Pack [0100000000003002][v0].nsp", "Orbit Expedition - Star Pack")]
    [TestCase("DREDGE [0100ABCD12340800][v0][1.0.1].nsp", "DREDGE")]
    [TestCase("Orbit Expedition (DLC) [0100000000003002][v0].xci", "Orbit Expedition (DLC)")]
    [TestCase("Plain Name.nsp", "Plain Name")]
    [TestCase("[0100000000003002][v0].nsp", "[0100000000003002][v0]")]
    [TestCase("Mid [tag] Name [v0].nsp", "Mid [tag] Name")]
    public void Of_DropsExtensionAndTrailingTags(string fileName, string expected) =>
        Assert.That(ReleaseName.Of(fileName), Is.EqualTo(expected));
}
