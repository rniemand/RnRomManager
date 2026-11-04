using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Tests.Configuration;

/// <summary>The small validators: connection string, admin, integrity, logging, sources.</summary>
public class OptionsValidatorTests
{
    [TestCase("Server=db;Database=rnrommanager", true)]
    [TestCase("x", true)]
    [TestCase("", false)]
    [TestCase("  ", false)]
    public void ConnectionString_IsRequired(string value, bool valid) =>
        Assert.That(new ConnectionStringsOptionsValidator().Validate(null, new() { Default = value }).Succeeded, Is.EqualTo(valid));

    [TestCase(0, true)]
    [TestCase(11, false)]
    [TestCase(12, true)]
    [TestCase(128, true)]
    [TestCase(129, false)]
    public void InitialPassword_EmptyOr12To128(int length, bool valid) =>
        Assert.That(new AdminOptionsValidator().Validate(null, new() { InitialPassword = new string('p', length) }).Succeeded, Is.EqualTo(valid));

    [Test]
    public void InitialPassword_FailureNeverContainsTheValue()
    {
        var result = new AdminOptionsValidator().Validate(null, new() { InitialPassword = "short-secret" + new string('x', 200) });
        Assert.That(result.FailureMessage, Does.Not.Contain("short-secret"));
    }

    [TestCase("03:00", true)]
    [TestCase("00:00", true)]
    [TestCase("23:59", true)]
    [TestCase("", true)]
    [TestCase("3:00", false)]
    [TestCase("24:00", false)]
    [TestCase("12:60", false)]
    [TestCase("03:00:00", false)]
    [TestCase("noon", false)]
    [TestCase(" ", false)]
    public void QuickCheckTime_HHmmOrEmpty(string value, bool valid) =>
        Assert.That(new IntegrityOptionsValidator().Validate(null, new() { QuickCheckTime = value }).Succeeded, Is.EqualTo(valid));

    [TestCase("Trace", true)]
    [TestCase("debug", true)]
    [TestCase("Info", true)]
    [TestCase("WARN", true)]
    [TestCase("Error", true)]
    [TestCase("Fatal", true)]
    [TestCase("Off", true)]
    [TestCase("Information", false)]
    [TestCase("", false)]
    public void MinLevel_IsAnNLogLevelName(string value, bool valid) =>
        Assert.That(new LoggingOptionsValidator().Validate(null, new() { MinLevel = value }).Succeeded, Is.EqualTo(valid));

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(365, true)]
    [TestCase(366, false)]
    [TestCase(-5, false)]
    public void RetentionDays_1To365(int value, bool valid) =>
        Assert.That(new LoggingOptionsValidator().Validate(null, new() { RetentionDays = value }).Succeeded, Is.EqualTo(valid));

    [TestCase("https://raw.githubusercontent.com/x", true)]
    [TestCase("http://localhost:9000/dats", true)]
    [TestCase("", false)]
    [TestCase("ftp://example.com", false)]
    [TestCase("/relative/path", false)]
    [TestCase("not a url", false)]
    public void DatBaseUrl_IsAbsoluteHttpUrl(string value, bool valid) =>
        Assert.That(new SourcesOptionsValidator().Validate(null, new() { DatBaseUrl = value }).Succeeded, Is.EqualTo(valid));

    [TestCase("https://thumbnails.libretro.com", true)]
    [TestCase("", true)]
    [TestCase("file:///c:/x", false)]
    [TestCase("thumbnails.libretro.com", false)]
    public void ThumbnailBaseUrl_IsAbsoluteHttpUrlOrEmpty(string value, bool valid) =>
        Assert.That(new SourcesOptionsValidator().Validate(null, new() { ThumbnailBaseUrl = value }).Succeeded, Is.EqualTo(valid));

    [Test]
    public void Defaults_AreValid()
    {
        Assert.That(new AdminOptionsValidator().Validate(null, new()).Succeeded, Is.True);
        Assert.That(new IntegrityOptionsValidator().Validate(null, new()).Succeeded, Is.True);
        Assert.That(new LoggingOptionsValidator().Validate(null, new()).Succeeded, Is.True);
        Assert.That(new SourcesOptionsValidator().Validate(null, new()).Succeeded, Is.True);
    }
}
