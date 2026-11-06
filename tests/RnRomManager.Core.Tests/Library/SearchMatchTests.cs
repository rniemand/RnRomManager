using RnRomManager.Core.Library;

namespace RnRomManager.Core.Tests.Library;

public class SearchMatchTests
{
    private static SearchMatch? Find(string[] words, string title = "Tetris", string[]? developers = null,
        string[]? publishers = null, string[]? genres = null) =>
        SearchMatch.Find(words, title, title, developers ?? [], publishers ?? [], genres ?? []);

    [Test]
    public void NoWordsOrATitleMatch_IsNull()
    {
        Assert.That(Find([], developers: ["Nintendo"]), Is.Null);
        Assert.That(Find(["tet", "nintendo"], developers: ["Nintendo"]), Is.Null, "any word in the title");
    }

    [Test]
    public void FirstMatchingValue_InFieldOrderDeveloperPublisherGenre()
    {
        Assert.That(Find(["puzzle"], developers: ["Bullet-Proof"], publishers: ["Puzzle Co"], genres: ["Puzzle"]),
            Is.EqualTo(new SearchMatch("publisher", "Puzzle Co")));
        Assert.That(Find(["nin"], developers: ["Bullet-Proof", "Nintendo R&D1"], publishers: ["Nintendo"]),
            Is.EqualTo(new SearchMatch("developer", "Nintendo R&D1")));
        Assert.That(Find(["action"], genres: ["Puzzle", "Action"]), Is.EqualTo(new SearchMatch("genre", "Action")));
    }

    [Test]
    public void IgnoresCaseAndAccents_LikeTheDatabase()
    {
        Assert.That(Find(["NINTENDO"], publishers: ["Nintendo"]), Is.EqualTo(new SearchMatch("publisher", "Nintendo")));
        Assert.That(Find(["pokemon"], publishers: ["Pokémon Co"]), Is.EqualTo(new SearchMatch("publisher", "Pokémon Co")));
        Assert.That(Find(["zzz"], publishers: ["Nintendo"]), Is.Null);
    }
}
