using System.Text.Json;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests;

/// <summary>Spec §23 <c>PATCH /api/games/{id}</c> body rules (S33).</summary>
public class GamePatchParsingTests
{
    private static (GamePatch Patch, Dictionary<string, string[]> Errors) Parse(string json) =>
        LibraryEndpoints.ParsePatch(JsonDocument.Parse(json).RootElement);

    [Test]
    public void EmptyObject_ChangesNothing()
    {
        var (patch, errors) = Parse("{}");

        Assert.That(errors, Is.Empty);
        Assert.That(patch, Is.EqualTo(new GamePatch()));
    }

    [Test]
    public void NullYearAndDescription_Clear_OtherFieldsStayUnchanged()
    {
        var (patch, errors) = Parse("""{"releaseYear":null,"description":null}""");

        Assert.That(errors, Is.Empty);
        Assert.That((patch.SetReleaseYear, patch.ReleaseYear, patch.SetDescription, patch.Description), Is.EqualTo((true, (int?)null, true, (string?)null)));
        Assert.That((patch.Title, patch.Developers, patch.Publishers, patch.Genres), Is.EqualTo(((string?)null, (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null)));
    }

    [Test]
    public void Values_AreTrimmed_ListsDistinctInOrder()
    {
        var (patch, errors) = Parse("""
            {"title":"  Legend of Zelda, The ","releaseYear":1993,"developers":[" Nintendo ","Nintendo","Capcom"],
             "publishers":[],"genres":["Action"],"description":"A classic."}
            """);

        Assert.That(errors, Is.Empty);
        Assert.That((patch.Title, patch.ReleaseYear, patch.Description), Is.EqualTo(("Legend of Zelda, The", (int?)1993, "A classic.")));
        Assert.That(patch.Developers, Is.EqualTo(new[] { "Nintendo", "Capcom" }));
        Assert.That(patch.Publishers, Is.Empty);
        Assert.That(patch.Genres, Is.EqualTo(new[] { "Action" }));
    }

    [TestCase("""{"title":null}""", "title")]
    [TestCase("""{"title":"   "}""", "title")]
    [TestCase("""{"title":".."}""", "title")]
    [TestCase("""{"title":5}""", "title")]
    [TestCase("""{"releaseYear":1969}""", "releaseYear")]
    [TestCase("""{"releaseYear":2101}""", "releaseYear")]
    [TestCase("""{"releaseYear":"1990"}""", "releaseYear")]
    [TestCase("""{"releaseYear":1990.5}""", "releaseYear")]
    [TestCase("""{"developers":null}""", "developers")]
    [TestCase("""{"publishers":[" "]}""", "publishers")]
    [TestCase("""{"genres":[1]}""", "genres")]
    [TestCase("""{"description":7}""", "description")]
    public void RejectsInvalidField(string json, string field) =>
        Assert.That(Parse(json).Errors, Does.ContainKey(field));

    [Test]
    public void Limits_TitleLength_ListSize_ItemLength_DescriptionLength()
    {
        Assert.That(Parse(JsonSerializer.Serialize(new { title = new string('x', 255) })).Errors, Is.Empty);
        Assert.That(Parse(JsonSerializer.Serialize(new { title = new string('x', 256) })).Errors, Does.ContainKey("title"));
        Assert.That(Parse(JsonSerializer.Serialize(new { genres = Enumerable.Range(0, 20).Select(i => $"g{i}") })).Errors, Is.Empty);
        Assert.That(Parse(JsonSerializer.Serialize(new { genres = Enumerable.Range(0, 21).Select(i => $"g{i}") })).Errors, Does.ContainKey("genres"));
        Assert.That(Parse(JsonSerializer.Serialize(new { genres = Enumerable.Repeat("same", 21) })).Errors, Is.Empty, "distinct before counting");
        Assert.That(Parse(JsonSerializer.Serialize(new { developers = new[] { new string('x', 256) } })).Errors, Does.ContainKey("developers"));
        Assert.That(Parse(JsonSerializer.Serialize(new { description = new string('x', 4000) })).Errors, Is.Empty);
        Assert.That(Parse(JsonSerializer.Serialize(new { description = new string('x', 4001) })).Errors, Does.ContainKey("description"));
    }

    [Test]
    public void NonObjectBody_IsRejected() => Assert.That(Parse("[]").Errors, Is.Not.Empty);
}
