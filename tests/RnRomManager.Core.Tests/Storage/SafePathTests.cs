using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Tests.Storage;

public class SafePathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rnrm-safepath", "roms");

    [TestCase("gb/T/Tetris.zip")]
    [TestCase("gb/A/../T/Tetris.zip")]
    [TestCase("./x.zip")]
    public void InsideRoot_ReturnsTheFullPath(string relative)
    {
        var resolved = SafePath.Resolve(Root, relative);

        Assert.That(resolved, Is.EqualTo(Path.GetFullPath(Path.Combine(Root, relative))));
        Assert.That(resolved, Does.StartWith(Root + Path.DirectorySeparatorChar));
    }

    [Test]
    public void RootWithTrailingSeparator_Works()
    {
        Assert.That(SafePath.Resolve(Root + Path.DirectorySeparatorChar, "x.zip"), Is.EqualTo(Path.Combine(Root, "x.zip")));
    }

    [TestCase("..")]
    [TestCase("../x.zip")]
    [TestCase("gb/../../x.zip")]
    [TestCase("../roms2/x.zip")] // sibling-prefix trick: /…/roms2 starts with /…/roms
    [TestCase("")]
    [TestCase(".")]
    [TestCase("gb/..")]
    [TestCase("x\0.zip")]
    public void TraversalOrRootItself_Throws(string relative)
    {
        Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, relative));
    }

    [Test]
    public void AbsolutePath_ThrowsEvenWhenItIsInsideRoot()
    {
        Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, Path.Combine(Root, "x.zip")));
        Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, Path.GetFullPath("/etc/passwd")));
    }

    [Test]
    public void BackslashTraversal_ThrowsOnWindows_IsAPlainFileNameElsewhere()
    {
        const string relative = @"gb\..\..\x.zip";

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, relative));
            Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, @"\x.zip"));
            Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, "C:x.zip"));
        }
        else
        {
            // On Linux '\' is an ordinary character: this is one file directly in root.
            Assert.That(SafePath.Resolve(Root, relative), Is.EqualTo(Path.Combine(Root, relative)));
        }
    }

    [Test]
    public void Exception_NamesRootAndInput()
    {
        var ex = Assert.Throws<UnsafePathException>(() => SafePath.Resolve(Root, "../x"));

        Assert.That((ex!.Root, ex.Relative), Is.EqualTo((Root, "../x")));
    }
}
