using RnRomManager.Core.Bundles;

namespace RnRomManager.Core.Tests.Bundles;

/// <summary>Bundle Creator B2–B4: the structural checks shared by queue create and replace.</summary>
public class BundleQueueValidationTests
{
    private const string Game = "8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha2 = "fedcba9876543210fedcba9876543210fedcba98";

    private static BundleEntryInput MainFile(string fileName = "a.gb", string sha = Sha) => new(Game, sha, null, "Game", fileName);

    private static BundleEntryInput SetFile(string name) => new(Game, Sha, new BundleMemberInput(Sha2, name), "Game", name);

    [TestCase(" Favourites ", "Favourites")]
    [TestCase("Pokémon", "Pokémon")]
    public void Name_IsTrimmed(string input, string expected)
    {
        string? name = input;
        Assert.That(BundleQueueValidation.ValidateName(ref name), Is.Null);
        Assert.That(name, Is.EqualTo(expected));
    }

    [Test]
    public void Name_Is1To100CodePoints()
    {
        string? blank = "   ", none = null, max = new string('a', 100), over = new string('a', 101);
        string? astral = string.Concat(Enumerable.Repeat("🎮", 100)), astralOver = string.Concat(Enumerable.Repeat("🎮", 101));
        string? broken = "a\uD83C";

        Assert.That(BundleQueueValidation.ValidateName(ref blank), Is.EqualTo("A name is required."));
        Assert.That(BundleQueueValidation.ValidateName(ref none), Is.EqualTo("A name is required."));
        Assert.That(BundleQueueValidation.ValidateName(ref max), Is.Null);
        Assert.That(BundleQueueValidation.ValidateName(ref over), Is.Not.Null);
        Assert.That(BundleQueueValidation.ValidateName(ref astral), Is.Null, "200 UTF-16 units, 100 code points");
        Assert.That(BundleQueueValidation.ValidateName(ref astralOver), Is.Not.Null);
        Assert.That(BundleQueueValidation.ValidateName(ref broken), Is.Not.Null);
    }

    [Test]
    public void Entries_AreKeptInOrder_WithMainAndSetFilesDistinct()
    {
        var (entries, errors) = BundleQueueValidation.ValidateEntries([SetFile("Track 2.bin"), MainFile(), SetFile("track 2.bin")]);

        Assert.That(errors, Is.Empty);
        Assert.That(entries.Select(e => (e.Member?.FileName, e.FileName)), Is.EqualTo(new (string?, string)[]
        {
            ("Track 2.bin", "Track 2.bin"), (null, "a.gb"), ("track 2.bin", "track 2.bin"),
        }));
        Assert.That(entries[0].GameId, Is.EqualTo(Guid.Parse(Game)));
    }

    [Test]
    public void Entries_RejectDuplicateIdentities_ButNotDuplicateDisplayNames()
    {
        var (_, duplicate) = BundleQueueValidation.ValidateEntries([MainFile(), MainFile(fileName: "other label.gb")]);
        var (entries, sameName) = BundleQueueValidation.ValidateEntries([MainFile(), MainFile(sha: Sha2)]);

        Assert.That(duplicate, Is.EqualTo(new Dictionary<string, string[]> { ["entries[1]"] = ["The same file as entries[0]."] }));
        Assert.That(sameName, Is.Empty);
        Assert.That(entries, Has.Count.EqualTo(2));
    }

    [Test]
    public void Entries_AreRequired_AndCappedAt500()
    {
        Assert.That(BundleQueueValidation.ValidateEntries(null).Errors.Keys, Is.EqualTo(new[] { "entries" }));
        Assert.That(BundleQueueValidation.ValidateEntries([]).Errors, Is.Empty);

        var max = Enumerable.Range(0, 500).Select(i => MainFile(sha: i.ToString("x40"))).ToList();
        Assert.That(BundleQueueValidation.ValidateEntries(max).Errors, Is.Empty);
        Assert.That(BundleQueueValidation.ValidateEntries([.. max, MainFile(sha: Sha2)]).Errors["entries"], Is.EqualTo(new[] { "At most 500 files." }));
    }

    [Test]
    public void Entries_NameEachMalformedField()
    {
        BundleEntryInput?[] input =
        [
            null,
            new(null, Sha, null, "G", "f"),
            new("00000000-0000-0000-0000-000000000000", Sha, null, "G", "f"),
            new("8c0f3b551a2b4c3d8e9f0a1b2c3d4e5f", Sha, null, "G", "f"),
            new(Game, Sha.ToUpperInvariant(), null, "G", "f"),
            new(Game, Sha, new BundleMemberInput("nope", "f"), "G", "f"),
            new(Game, Sha, new BundleMemberInput(Sha2, ""), "G", "f"),
            new(Game, Sha2, null, "", new string('f', 256)),
        ];

        var (entries, errors) = BundleQueueValidation.ValidateEntries(input);

        Assert.That(entries, Is.Empty);
        Assert.That(errors.Keys, Is.EquivalentTo(new[]
        {
            "entries[0]", "entries[1].gameId", "entries[2].gameId", "entries[3].gameId", "entries[4].releaseSha1",
            "entries[5].member.sha1", "entries[6].member.fileName", "entries[7].gameTitle", "entries[7].fileName",
        }));
    }

    [Test]
    public void Labels_Allow255CodePoints()
    {
        var label = string.Concat(Enumerable.Repeat("é", 255));
        var astral = string.Concat(Enumerable.Repeat("🎮", 255));

        Assert.That(BundleQueueValidation.ValidateEntries([new BundleEntryInput(Game, Sha, new BundleMemberInput(Sha2, astral), label, astral)]).Errors, Is.Empty);
        Assert.That(BundleQueueValidation.ValidateEntries([new BundleEntryInput(Game, Sha, null, label + "é", "f")]).Errors.Keys,
            Is.EqualTo(new[] { "entries[0].gameTitle" }));
    }
}
