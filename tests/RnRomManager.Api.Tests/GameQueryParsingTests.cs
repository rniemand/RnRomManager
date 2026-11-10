using NSubstitute;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Tests;

/// <summary>Spec §23 <c>/api/games</c> query defaults and validation (S32).</summary>
public class GameQueryParsingTests
{
    private static readonly ISystemRegistry Systems = CreateSystems();

    private static ISystemRegistry CreateSystems()
    {
        var systems = Substitute.For<ISystemRegistry>();
        systems.ById(Arg.Any<string>()).Returns((ISystemHandler?)null);
        systems.ById("gb").Returns(Substitute.For<ISystemHandler>());
        return systems;
    }

    private static (GameQuery Query, Dictionary<string, string[]> Errors) Parse(string? system = null, string? letter = null,
        string? region = null, string? q = null, string? sort = null, string? dir = null, int? page = null, int? pageSize = null,
        string? favourite = null, string? collection = null, string? notInCollection = null) =>
        LibraryEndpoints.ParseQuery(system, letter, region, q, sort, dir, page, pageSize, Systems, favourite, collection, notInCollection);

    [Test]
    public void Defaults_TitleAscending_FirstPageOf48_NoFilters()
    {
        var (query, errors) = Parse();

        Assert.That(errors, Is.Empty);
        Assert.That(query, Is.EqualTo(new GameQuery { Sort = GameSort.Title, Descending = false, Page = 1, PageSize = 48 }));
    }

    [TestCase("title", null, false)]
    [TestCase("year", null, false)]
    [TestCase("added", null, true)]
    [TestCase("added", "asc", false)]
    [TestCase("title", "desc", true)]
    [TestCase("rating", null, true)]
    [TestCase("rating", "asc", false)]
    [TestCase("relevance", null, false)]
    [TestCase("relevance", "desc", false)]
    public void Direction_DefaultsPerSort(string sort, string? dir, bool descending) =>
        Assert.That(Parse(sort: sort, dir: dir).Query.Descending, Is.EqualTo(descending));

    [Test]
    public void Collection_IsAGuid_AndAddedStillDefaultsToDescending()
    {
        var id = Guid.NewGuid();
        var (query, errors) = Parse(collection: id.ToString(), sort: "added");

        Assert.That(errors, Is.Empty);
        Assert.That((query.CollectionId, query.Descending), Is.EqualTo(((Guid?)id, true)));
        Assert.That(Parse().Query.CollectionId, Is.Null);
    }

    [TestCase("not-a-guid")]
    [TestCase("")]
    public void Collection_Malformed_IsAFieldError(string collection) =>
        Assert.That(Parse(collection: collection).Errors.Keys, Is.EquivalentTo(new[] { "collection" }));

    [Test]
    public void NotInCollection_IsAGuid_P1()
    {
        var id = Guid.NewGuid();
        var (query, errors) = Parse(notInCollection: id.ToString());

        Assert.That(errors, Is.Empty);
        Assert.That((query.NotInCollectionId, query.CollectionId), Is.EqualTo(((Guid?)id, (Guid?)null)));
        Assert.That(Parse().Query.NotInCollectionId, Is.Null);
    }

    [TestCase("abc")]
    [TestCase("")]
    public void NotInCollection_Malformed_IsAFieldError(string value)
    {
        var errors = Parse(notInCollection: value).Errors;
        Assert.That(errors.Keys, Is.EquivalentTo(new[] { "notInCollection" }));
        Assert.That(errors["notInCollection"], Is.EqualTo(new[] { "Must be a collection id." }));
    }

    [Test]
    public void Normalises_LetterCase_TrimsSearchAndRegion_BlankMeansNoFilter()
    {
        var (query, errors) = Parse(system: "gb", letter: "t", region: " USA ", q: "  tetris ");
        Assert.That(errors, Is.Empty);
        Assert.That((query.System, query.Letter, query.Region, query.Search), Is.EqualTo(("gb", "T", "USA", "tetris")));

        var blank = Parse(region: " ", q: "   ").Query;
        Assert.That((blank.Region, blank.Search), Is.EqualTo(((string?)null, (string?)null)));
        Assert.That(Parse(letter: "#").Query.Letter, Is.EqualTo("#"));
    }

    [Test]
    public void Search_SplitsIntoDistinctWords_AtMostEight()
    {
        var (query, errors) = Parse(q: "  zelda	Link  ZELDA awakening ");

        Assert.That(errors, Is.Empty);
        Assert.That(query.Words, Is.EqualTo(new[] { "zelda", "Link", "awakening" }));
        Assert.That(query.Phrase, Is.EqualTo("zelda Link ZELDA awakening"));
        Assert.That(Parse(q: "a b c d e f g h").Errors, Is.Empty);
        Assert.That(Parse(q: "a b c d e f g h a").Errors, Is.Empty, "duplicates don't count");
        Assert.That(Parse(q: "a b c d e f g h i").Errors["q"], Is.EqualTo(new[] { "At most 8 words." }));
    }

    [TestCase("rating", GameSort.Rating)]
    [TestCase("relevance", GameSort.Relevance)]
    public void NewSorts_AreAccepted(string sort, GameSort expected) =>
        Assert.That(Parse(sort: sort).Query.Sort, Is.EqualTo(expected));

    [TestCase("true", true)]
    [TestCase("false", false)]
    public void Favourite_TrueOrFalse(string value, bool expected) =>
        Assert.That(Parse(favourite: value).Query.Favourite, Is.EqualTo(expected));

    [TestCase("True")]
    [TestCase("1")]
    [TestCase("yes")]
    [TestCase("")]
    public void Favourite_OtherValues_AreValidationFailed(string value) =>
        Assert.That(Parse(favourite: value).Errors["favourite"], Is.EqualTo(new[] { "Must be true or false." }));

    [TestCase("page", 0, null)]
    [TestCase("pageSize", null, 201)]
    public void RejectsPagingOutOfBounds(string field, int? page, int? pageSize) =>
        Assert.That(Parse(page: page, pageSize: pageSize).Errors, Does.ContainKey(field));

    [Test]
    public void AcceptsMaxPageSize() => Assert.That(Parse(pageSize: 200).Errors, Is.Empty);

    [Test]
    public void RejectsUnknownValues()
    {
        Assert.That(Parse(system: "md").Errors, Does.ContainKey("system"));
        Assert.That(Parse(letter: "AA").Errors, Does.ContainKey("letter"));
        Assert.That(Parse(letter: "É").Errors, Does.ContainKey("letter"));
        Assert.That(Parse(q: new string('x', 256)).Errors, Does.ContainKey("q"));
        Assert.That(Parse(sort: "Title").Errors, Does.ContainKey("sort"));
        Assert.That(Parse(dir: "DESC").Errors, Does.ContainKey("dir"));
    }
}
