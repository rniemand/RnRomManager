using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.CodeStyle;

/// <summary>S2/S3 guard: the repo breaks no structural rule of <c>docs/style-guide.md</c> beyond the shrinking baseline.</summary>
public class CodeStyleTests
{
    private const string BaselinePath = "tests/RnRomManager.Api.Tests/CodeStyle/code-style-baseline.txt";

    [Test]
    public void Repository_MatchesTheBaseline()
    {
        var root = RepoPaths.Root();
        var baseline = CodeStyleScanner.ParseBaseline(File.ReadAllText(Path.Combine(root, BaselinePath)));

        var (added, stale) = CodeStyleScanner.Compare(CodeStyleScanner.ScanRepo(root), baseline);

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.Empty,
                "New violations of docs/style-guide.md: fix them, never add them to the baseline.\n" + string.Join("\n", added));
            Assert.That(stale, Is.Empty,
                $"Stale entries in {BaselinePath} (now clean): delete these lines.\n" + string.Join("\n", stale));
        });
    }
}
