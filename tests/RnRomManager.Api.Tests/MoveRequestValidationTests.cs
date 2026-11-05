using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests;

/// <summary>Spec §23 <c>move</c> body: <c>{targetGameId}</c> or <c>{newGameTitle}</c> (S34).</summary>
public class MoveRequestValidationTests
{
    [Test]
    public void AcceptsTargetOrNewTitle()
    {
        Assert.That(LibraryEndpoints.Validate(new MoveReleaseRequest(Guid.NewGuid(), null)), Is.Empty);
        Assert.That(LibraryEndpoints.Validate(new MoveReleaseRequest(null, " New Game ")), Is.Empty);
    }

    [TestCase(false, null, "targetGameId")]
    [TestCase(true, "New", "targetGameId")]
    [TestCase(false, "  ", "newGameTitle")]
    [TestCase(false, "..", "newGameTitle")]
    public void RejectsNeitherBothOrBadTitle(bool hasTarget, string? title, string field) =>
        Assert.That(LibraryEndpoints.Validate(new MoveReleaseRequest(hasTarget ? Guid.NewGuid() : null, title)), Does.ContainKey(field));

    [Test]
    public void EmptyGuid_CountsAsMissing() =>
        Assert.That(LibraryEndpoints.Validate(new MoveReleaseRequest(Guid.Empty, null)), Does.ContainKey("targetGameId"));

    [Test]
    public void TitleOver255_IsRejected() =>
        Assert.That(LibraryEndpoints.Validate(new MoveReleaseRequest(null, new string('x', 256))), Does.ContainKey("newGameTitle"));
}
