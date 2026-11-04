using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Tests.Configuration;

public class PathsOptionsValidatorTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rnrm");

    private static PathsOptions Valid() => new()
    {
        Roms = Path.Combine(Root, "roms"),
        Ingest = Path.Combine(Root, "ingest"),
        Logs = Path.Combine(Root, "logs"),
        Cache = Path.Combine(Root, "cache"),
    };

    private static IEnumerable<string> Failures(PathsOptions options) =>
        new PathsOptionsValidator().Validate(null, options).Failures ?? [];

    [Test]
    public void DistinctAbsolutePaths_AreValid() =>
        Assert.That(new PathsOptionsValidator().Validate(null, Valid()).Succeeded, Is.True);

    [Test]
    public void SiblingWithSamePrefix_IsNotNested()
    {
        var options = Valid();
        options.Ingest = options.Roms + "2";
        Assert.That(Failures(options), Is.Empty);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void MissingPath_Fails(string value)
    {
        var options = Valid();
        options.Logs = value;
        Assert.That(Failures(options), Has.Some.EqualTo("Paths:Logs is required."));
    }

    [TestCase("relative")]
    [TestCase("./roms")]
    [TestCase("../roms")]
    public void RelativePath_Fails(string value)
    {
        var options = Valid();
        options.Roms = value;
        Assert.That(Failures(options), Has.Some.StartsWith("Paths:Roms must be an absolute path"));
    }

    [Test]
    public void DriveRelativeOrRootedOnlyPath_FailsOnWindows()
    {
        Assume.That(OperatingSystem.IsWindows(), "Rooted-but-not-qualified paths only exist on Windows.");
        var options = Valid();
        options.Roms = @"\app\roms";
        Assert.That(Failures(options), Has.Some.StartsWith("Paths:Roms must be an absolute path"));
    }

    [Test]
    public void DuplicatePaths_Fail()
    {
        var options = Valid();
        options.Cache = options.Roms;
        Assert.That(Failures(options), Has.Some.EqualTo("Paths:Roms and Paths:Cache must be different paths."));
    }

    [Test]
    public void DuplicatePaths_IgnoringCaseAndTrailingSeparator_Fail()
    {
        var options = Valid();
        options.Cache = options.Roms.ToUpperInvariant() + Path.DirectorySeparatorChar;
        Assert.That(Failures(options), Has.Some.EqualTo("Paths:Roms and Paths:Cache must be different paths."));
    }

    [Test]
    public void PathInsideAnother_Fails()
    {
        var options = Valid();
        options.Cache = Path.Combine(options.Roms, "cache");
        Assert.That(Failures(options), Has.Some.EqualTo("Paths:Cache must not be inside Paths:Roms."));
    }

    [Test]
    public void PathContainingAnother_Fails()
    {
        var options = Valid();
        options.Roms = Root;
        Assert.That(Failures(options), Has.Some.EqualTo("Paths:Ingest must not be inside Paths:Roms."));
    }

    [Test]
    public void NestedViaDotDot_Fails()
    {
        var options = Valid();
        options.Cache = Path.Combine(options.Logs, "x", "..", "cache");
        Assert.That(Failures(options), Has.Some.EqualTo("Paths:Cache must not be inside Paths:Logs."));
    }
}
