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
        string? region = null, string? q = null, string? sort = null, string? dir = null, int? page = null, int? pageSize = null) =>
        LibraryEndpoints.ParseQuery(system, letter, region, q, sort, dir, page, pageSize, Systems);

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
    public void Direction_DefaultsPerSort(string sort, string? dir, bool descending) =>
        Assert.That(Parse(sort: sort, dir: dir).Query.Descending, Is.EqualTo(descending));

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

    [TestCase("page", 0, null)]
    [TestCase("pageSize", null, 201)]
    public void RejectsPagingOutOfBounds(string field, int? page, int? pageSize) =>
        Assert.That(Parse(page: page, pageSize: pageSize).Errors, Does.ContainKey(field));

    [Test]
    public void AcceptsMaxPageSize() => Assert.That(Parse(pageSize: 200).Errors, Is.Empty);

    [Test]
    public void RejectsUnknownValues()
    {
        Assert.That(Parse(system: "nes").Errors, Does.ContainKey("system"));
        Assert.That(Parse(letter: "AA").Errors, Does.ContainKey("letter"));
        Assert.That(Parse(letter: "É").Errors, Does.ContainKey("letter"));
        Assert.That(Parse(q: new string('x', 256)).Errors, Does.ContainKey("q"));
        Assert.That(Parse(sort: "Title").Errors, Does.ContainKey("sort"));
        Assert.That(Parse(dir: "DESC").Errors, Does.ContainKey("dir"));
    }
}
