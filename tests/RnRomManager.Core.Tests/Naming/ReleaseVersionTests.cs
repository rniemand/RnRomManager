using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Naming;

/// <summary>Game updates B2/B3: the version tag in a file name.</summary>
public class ReleaseVersionTests
{
    [TestCase("DREDGE[UPD][1.0.1].nsp", "1.0.1")]
    [TestCase("X [1.0].nsp", "1.0")]
    [TestCase("X [1.2.3.4].nsp", "1.2.3.4")]
    [TestCase("X [0100ABCD12340800][v0][2.10.0].nsp", "2.10.0")]
    [TestCase("X [1.0.1][2.0.0].nsp", "1.0.1")]
    [TestCase("X [v65536].nsp", null)]
    [TestCase("X [2024].nsp", null)]
    [TestCase("X [1.2.3.4.5].nsp", null)]
    [TestCase("X [1.a].nsp", null)]
    [TestCase("X [1234567890.1].nsp", null)]
    [TestCase("X [١.٢].nsp", null)] // non-ASCII digits
    [TestCase("X 1.0.1.nsp", null)]
    public void From_ReadsTheFirstDottedTag(string fileName, string? expected) =>
        Assert.That(ReleaseVersion.From(fileName), Is.EqualTo(expected));

    [Test]
    public void For_OnlyUpdatesGetAVersion()
    {
        Assert.That(ReleaseVersion.For(["Update"], "X[UPD][1.0.1].nsp"), Is.EqualTo("1.0.1"));
        Assert.That(ReleaseVersion.For(["Update"], "X[UPD].nsp"), Is.Null);
        Assert.That(ReleaseVersion.For([], "X[UPD][1.0.1].nsp"), Is.Null);
        Assert.That(ReleaseVersion.For(["DLC"], "X[1.0.1].nsp"), Is.Null);
        Assert.That(ReleaseVersion.For(["update"], "X[1.0.1].nsp"), Is.Null, "ordinal");
    }
}
