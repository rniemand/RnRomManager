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

    [TestCase("https://raw.githubusercontent.com/blawar/titledb/master/US.en.json", true)]
    [TestCase("http://localhost:9000/US.en.json", true)]
    [TestCase("", false)]
    [TestCase("ftp://example.com/US.en.json", false)]
    [TestCase("/relative/US.en.json", false)]
    public void TitleDbUrl_IsAbsoluteHttpUrl(string value, bool valid) =>
        Assert.That(new SourcesOptionsValidator().Validate(null, new() { TitleDbUrl = value }).Succeeded, Is.EqualTo(valid));

    [Test]
    public void TitleDbUrl_DefaultsToRawUsEnglish() =>
        Assert.That(new SourcesOptions().TitleDbUrl, Is.EqualTo("https://raw.githubusercontent.com/blawar/titledb/master/US.en.json"));

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

    [Test]
    public void GenesisCorePath_EmptyOrAbsolute()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "genesis");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new()).Succeeded, Is.True, "blank disables");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { GenesisCorePath = absolute }).Succeeded, Is.True);
        var relative = new EmulatorOptionsValidator().Validate(null, new() { GenesisCorePath = "genesis" });
        Assert.That(relative.Succeeded, Is.False);
        Assert.That(relative.FailureMessage, Does.Contain("Emulator:GenesisCorePath"));
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { GenesisCorePath = " " }).Succeeded, Is.False, "whitespace isn't blank");
    }

    [Test]
    public void PicodriveCorePath_EmptyOrAbsolute()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "picodrive");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { PicodriveCorePath = absolute }).Succeeded, Is.True);
        Assert.That(new EmulatorOptionsValidator().Validate(null,
            new() { PicodriveCorePath = absolute, GenesisCorePath = absolute }).Succeeded, Is.True, "may share the Genesis folder");
        var relative = new EmulatorOptionsValidator().Validate(null, new() { PicodriveCorePath = "picodrive" });
        Assert.That(relative.Succeeded, Is.False);
        Assert.That(relative.FailureMessage, Does.Contain("Emulator:PicodriveCorePath"));
        Assert.That(relative.FailureMessage, Does.Not.Contain("Emulator:GenesisCorePath"));
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { PicodriveCorePath = " " }).Succeeded, Is.False, "whitespace isn't blank");
    }

    [Test]
    public void Snes9xCorePath_EmptyOrAbsolute()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "snes9x");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { Snes9xCorePath = "" }).Succeeded, Is.True, "blank disables");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { Snes9xCorePath = absolute }).Succeeded, Is.True);
        Assert.That(new EmulatorOptionsValidator().Validate(null,
            new() { Snes9xCorePath = absolute, GenesisCorePath = absolute, PicodriveCorePath = absolute }).Succeeded,
            Is.True, "may share the other operator cores' folder");
        var relative = new EmulatorOptionsValidator().Validate(null, new() { Snes9xCorePath = "snes9x" });
        Assert.That(relative.Succeeded, Is.False);
        Assert.That(relative.FailureMessage, Does.Contain("Emulator:Snes9xCorePath"));
        Assert.That(relative.FailureMessage, Does.Not.Contain("Emulator:GenesisCorePath"));
        Assert.That(relative.FailureMessage, Does.Not.Contain("Emulator:PicodriveCorePath"));
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { Snes9xCorePath = " " }).Succeeded, Is.False, "whitespace isn't blank");
    }

    [Test]
    public void OperaCorePath_EmptyOrAbsolute()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "opera");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { OperaCorePath = "" }).Succeeded, Is.True, "blank disables");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { OperaCorePath = absolute }).Succeeded, Is.True);
        Assert.That(new EmulatorOptionsValidator().Validate(null,
            new() { OperaCorePath = absolute, GenesisCorePath = absolute, PicodriveCorePath = absolute, Snes9xCorePath = absolute }).Succeeded,
            Is.True, "may share the other operator cores' folder");
        var relative = new EmulatorOptionsValidator().Validate(null, new() { OperaCorePath = "opera" });
        Assert.That(relative.Succeeded, Is.False);
        Assert.That(relative.FailureMessage, Does.Contain("Emulator:OperaCorePath"));
        Assert.That(relative.FailureMessage, Does.Not.Contain("Emulator:Snes9xCorePath"));
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { OperaCorePath = " " }).Succeeded, Is.False, "whitespace isn't blank");
    }

    [Test]
    public void ViceCorePath_EmptyOrAbsolute()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "vice");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { ViceCorePath = "" }).Succeeded, Is.True, "blank disables");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { ViceCorePath = absolute }).Succeeded, Is.True);
        Assert.That(new EmulatorOptionsValidator().Validate(null,
            new() { ViceCorePath = absolute, GenesisCorePath = absolute, PicodriveCorePath = absolute, Snes9xCorePath = absolute, OperaCorePath = absolute }).Succeeded,
            Is.True, "may share the other operator cores' folder");
        var relative = new EmulatorOptionsValidator().Validate(null, new() { ViceCorePath = "vice" });
        Assert.That(relative.Succeeded, Is.False);
        Assert.That(relative.FailureMessage, Does.Contain("Emulator:ViceCorePath"));
        Assert.That(relative.FailureMessage, Does.Not.Contain("Emulator:Snes9xCorePath"));
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { ViceCorePath = " " }).Succeeded, Is.False, "whitespace isn't blank");
    }

    [Test]
    public void Mupen64PlusCorePath_EmptyOrAbsolute()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "n64");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { Mupen64PlusCorePath = "" }).Succeeded, Is.True, "blank disables");
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { Mupen64PlusCorePath = absolute, ViceCorePath = absolute }).Succeeded,
            Is.True, "may share the other operator cores' folder");
        var relative = new EmulatorOptionsValidator().Validate(null, new() { Mupen64PlusCorePath = "n64" });
        Assert.That(relative.Succeeded, Is.False);
        Assert.That(relative.FailureMessage, Does.Contain("Emulator:Mupen64PlusCorePath"));
        Assert.That(new EmulatorOptionsValidator().Validate(null, new() { Mupen64PlusCorePath = " " }).Succeeded, Is.False, "whitespace isn't blank");
    }
}
