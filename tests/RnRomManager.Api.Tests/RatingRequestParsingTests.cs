using System.Text.Json;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests;

/// <summary><c>PATCH /api/games/{id}/rating</c> body rules (B5).</summary>
public class RatingRequestParsingTests
{
    private static (GamePatch Patch, Dictionary<string, string[]> Errors) Parse(string json) =>
        LibraryEndpoints.ParseRating(JsonDocument.Parse(json).RootElement);

    [TestCase("""{"rating":4}""", true, 4, null)]
    [TestCase("""{"rating":null}""", true, null, null)]
    [TestCase("""{"favourite":true}""", false, null, true)]
    [TestCase("""{"rating":1,"favourite":false}""", true, 1, false)]
    [TestCase("""{"rating":5,"title":"ignored"}""", true, 5, null)]
    public void ValidBodies(string json, bool setRating, int? rating, bool? favourite)
    {
        var (patch, errors) = Parse(json);

        Assert.That(errors, Is.Empty);
        Assert.That(patch, Is.EqualTo(new GamePatch { SetRating = setRating, Rating = rating, Favourite = favourite }));
    }

    [TestCase("[]", "")]
    [TestCase("3", "")]
    [TestCase("{}", "")]
    [TestCase("""{"title":"x"}""", "")]
    [TestCase("""{"rating":0}""", "rating")]
    [TestCase("""{"rating":6}""", "rating")]
    [TestCase("""{"rating":2.5}""", "rating")]
    [TestCase("""{"rating":"3"}""", "rating")]
    [TestCase("""{"rating":true}""", "rating")]
    [TestCase("""{"favourite":"true"}""", "favourite")]
    [TestCase("""{"favourite":1}""", "favourite")]
    [TestCase("""{"favourite":null}""", "favourite")]
    public void InvalidBodies_NameTheField(string json, string field)
    {
        var (_, errors) = Parse(json);

        Assert.That(errors.Keys, Is.EqualTo(new[] { field }));
    }
}
