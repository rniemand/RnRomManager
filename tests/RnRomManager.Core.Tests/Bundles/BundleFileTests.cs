using RnRomManager.Core.Bundles;

namespace RnRomManager.Core.Tests.Bundles;

/// <summary>Bundle Creator B3: identity structure and the code-point rule.</summary>
public class BundleFileTests
{
    private static readonly Guid Game = Guid.Parse("8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f");
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [TestCase("0123456789abcdef0123456789abcdef01234567", true)]
    [TestCase("0123456789ABCDEF0123456789abcdef01234567", false)]
    [TestCase("0123456789abcdef0123456789abcdef0123456", false)]
    [TestCase("0123456789abcdef0123456789abcdef012345678", false)]
    [TestCase("g123456789abcdef0123456789abcdef01234567", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsSha1_IsLowercase40Hex(string? value, bool expected) =>
        Assert.That(BundleFile.IsSha1(value), Is.EqualTo(expected));

    [Test]
    public void CodePoints_CountsRunes_AndRejectsUnpairedSurrogates()
    {
        Assert.That(BundleFile.CodePoints("abc"), Is.EqualTo(3));
        Assert.That(BundleFile.CodePoints("Pokémon"), Is.EqualTo(7));
        Assert.That(BundleFile.CodePoints("🎮🎮"), Is.EqualTo(2), "astral characters are one code point each");
        Assert.That(BundleFile.CodePoints("é"), Is.EqualTo(2), "combining marks count separately, like VARCHAR");
        Assert.That(BundleFile.CodePoints("a\uD83C"), Is.EqualTo(-1));
        Assert.That(BundleFile.CodePoints("\uDFAEa"), Is.EqualTo(-1));
        Assert.That(BundleFile.CodePoints(""), Is.EqualTo(0));
    }

    [Test]
    public void IsText_AllowsUpTo255CodePoints()
    {
        Assert.That(BundleFile.IsText(new string('a', 255)), Is.True);
        Assert.That(BundleFile.IsText(new string('a', 256)), Is.False);
        Assert.That(BundleFile.IsText(string.Concat(Enumerable.Repeat("🎮", 255))), Is.True, "510 UTF-16 units");
        Assert.That(BundleFile.IsText(string.Concat(Enumerable.Repeat("🎮", 256))), Is.False);
        Assert.That(BundleFile.IsText(""), Is.False);
        Assert.That(BundleFile.IsText(null), Is.False);
        Assert.That(BundleFile.IsText("a\uD83C"), Is.False);
    }

    [Test]
    public void Validate_AcceptsWellFormedMainAndSetFiles()
    {
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Game, Sha, null)), Is.Null);
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Game, Sha, new BundleMember(Sha, "Track 2.bin"))), Is.Null);
    }

    [Test]
    public void Validate_NamesTheFirstProblem()
    {
        Assert.That(BundleFile.Validate(null), Is.EqualTo("is required."));
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Guid.Empty, Sha, null)), Does.StartWith("gameId"));
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Game, Sha.ToUpperInvariant(), null)), Does.StartWith("releaseSha1"));
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Game, Sha, new BundleMember("x", "a.bin"))), Does.StartWith("member.sha1"));
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Game, Sha, new BundleMember(Sha, ""))), Does.StartWith("member.fileName"));
        Assert.That(BundleFile.Validate(new BundleFileIdentity(Game, Sha, new BundleMember(Sha, new string('a', 256)))), Does.StartWith("member.fileName"));
    }

    [Test]
    public void Key_SeparatesMainFromSetFiles_AndComparesNamesExactly()
    {
        var main = BundleFile.Key(new BundleFileIdentity(Game, Sha, null));
        var member = BundleFile.Key(new BundleFileIdentity(Game, Sha, new BundleMember(Sha, "Track 2.bin")));
        var otherCase = BundleFile.Key(new BundleFileIdentity(Game, Sha, new BundleMember(Sha, "track 2.bin")));

        Assert.That(new[] { main, member, otherCase }, Is.Unique);
        Assert.That(BundleFile.Key(new BundleFileIdentity(Game, Sha, new BundleMember(Sha, "Track 2.bin"))), Is.EqualTo(member));
    }

    [Test]
    public void DownloadUrl_IsTheNormalSingleFileRoute()
    {
        Assert.That(new BundleFileTarget(Game, "T", 12, null, "a.gb").DownloadUrl, Is.EqualTo($"/api/games/{Game}/roms/12/download"));
        Assert.That(new BundleFileTarget(Game, "T", 12, 34, "a.bin").DownloadUrl, Is.EqualTo($"/api/games/{Game}/roms/12/files/34/download"));
    }
}
