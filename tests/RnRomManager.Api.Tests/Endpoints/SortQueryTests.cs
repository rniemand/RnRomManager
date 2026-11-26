using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>D3: <c>dir</c> parsing keeps each caller's missing-value default and the shared error message.</summary>
public class SortQueryTests
{
    [TestCase("asc", null, false)]
    [TestCase("DESC", null, true)]
    [TestCase("Asc", true, false)]
    [TestCase(null, null, null)]
    [TestCase(null, false, false)]
    [TestCase(null, true, true)]
    public void Valid_or_missing_dir_parses_without_error(string? dir, bool? whenMissing, bool? expected)
    {
        var errors = new Dictionary<string, string[]>();

        Assert.That(SortQuery.ParseDir(dir, errors, whenMissing), Is.EqualTo(expected));
        Assert.That(errors, Is.Empty);
    }

    [TestCase("up")]
    [TestCase("")]
    public void Invalid_dir_adds_the_error(string dir)
    {
        var errors = new Dictionary<string, string[]>();

        Assert.That(SortQuery.ParseDir(dir, errors, whenMissing: false), Is.Null);
        Assert.That(errors["dir"], Is.EqualTo(new[] { "Must be asc or desc." }));
    }
}
