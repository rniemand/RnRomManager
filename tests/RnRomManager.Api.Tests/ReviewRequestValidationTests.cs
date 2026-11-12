using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Api.Tests;

public class ReviewRequestValidationTests
{
    private static ReviewResolutionRequest Manual => new() { Action = "Manual", Title = "My Game", Regions = [], Languages = [], Flags = [] };

    private static IEnumerable<TestCaseData> InvalidCases()
    {
        yield return new(new ReviewResolutionRequest(), "action");
        yield return new(new ReviewResolutionRequest { Action = "Dismiss" }, "action");
        yield return new(new ReviewResolutionRequest { Action = "Dat", DatSystem = "gb" }, "datGameName");
        yield return new(new ReviewResolutionRequest { Action = "Dat", DatGameName = "Test" }, "datSystem");
        yield return new(new ReviewResolutionRequest { Action = "Attach", Flag = "Hack" }, "targetGameId");
        yield return new(new ReviewResolutionRequest { Action = "Attach", Flag = "Other", TargetGameId = Guid.NewGuid() }, "flag");
        yield return new(Manual with { Title = "   " }, "title");
        yield return new(Manual with { Title = new string('x', 256) }, "title");
        yield return new(Manual with { Title = ".." }, "title");
        yield return new(Manual with { Regions = null }, "regions");
        yield return new(Manual with { Regions = [" "] }, "regions");
        yield return new(Manual with { Languages = [null!] }, "languages");
        yield return new(Manual with { Flags = null }, "flags");
        yield return new(Manual with { Revision = new string('x', 33) }, "revision");
        yield return new(Manual with { Update = true }, "update");
        yield return new(new ReviewResolutionRequest { Action = "Attach", TargetGameId = Guid.NewGuid(), Flag = "None", Update = true }, "update");
    }

    [TestCaseSource(nameof(InvalidCases))]
    public void RejectsInvalidFields(ReviewResolutionRequest request, string field) =>
        Assert.That(ReviewEndpoints.Validate(request), Does.ContainKey(field));

    [Test]
    public void AcceptsValidActionShapes()
    {
        foreach (var request in new[]
        {
            Manual, Manual with { Title = "Sanitize: this", Revision = "Rev 2", Regions = ["USA"], Languages = ["En"], Flags = ["Homebrew"] },
            new ReviewResolutionRequest { Action = "Dat", DatSystem = "gb", DatGameName = "Tetris (World)" },
            new ReviewResolutionRequest { Action = "Dat", DatSystem = "switch", DatGameName = "X [0100000000010000]", Update = true },
            Manual with { Update = false },
            new ReviewResolutionRequest { Action = "Attach", TargetGameId = Guid.NewGuid(), Flag = "None" },
            new ReviewResolutionRequest { Action = "Attach", TargetGameId = Guid.NewGuid(), Flag = "Hack" },
            new ReviewResolutionRequest { Action = "Attach", TargetGameId = Guid.NewGuid(), Flag = "Translated" },
        }) Assert.That(ReviewEndpoints.Validate(request), Is.Empty);
    }
}
