using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Tests.Storage;

public class FileNameRulesTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("rnrm-names-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    [TestCase("Tetris.zip", 2, "Tetris (2).zip")]
    [TestCase("Tetris (World) (Rev 1).gb", 3, "Tetris (World) (Rev 1) (3).gb")]
    [TestCase("archive.tar.gz", 2, "archive.tar (2).gz")]
    [TestCase("README", 2, "README (2)")]
    public void WithSuffix_GoesBeforeTheExtension(string name, int n, string expected) =>
        Assert.That(FileNameRules.WithSuffix(name, n), Is.EqualTo(expected));

    [Test]
    public void FirstFree_KeepsAFreeName()
    {
        Assert.That(FileNameRules.FirstFree("a.gb", _ => false), Is.EqualTo("a.gb"));
    }

    [Test]
    public void FirstFree_CountsUpFromTwo()
    {
        var taken = new HashSet<string> { "a.gb", "a (2).gb", "a (3).gb" };

        Assert.That(FileNameRules.FirstFree("a.gb", taken.Contains), Is.EqualTo("a (4).gb"));
    }

    [Test]
    public void FirstFreeIn_IgnoresCase_AndCountsFolders()
    {
        File.WriteAllText(Path.Combine(_dir, "Tetris.zip"), "");
        Directory.CreateDirectory(Path.Combine(_dir, "TETRIS (2).ZIP"));

        Assert.That(FileNameRules.FirstFreeIn(_dir, "tetris.zip"), Is.EqualTo("tetris (3).zip"));
    }

    [Test]
    public void FirstFreeIn_MissingDirectory_KeepsTheName()
    {
        Assert.That(FileNameRules.FirstFreeIn(Path.Combine(_dir, "nope"), "a.gb"), Is.EqualTo("a.gb"));
    }
}
