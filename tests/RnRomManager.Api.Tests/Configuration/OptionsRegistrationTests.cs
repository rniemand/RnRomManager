using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Configuration;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Tests.Configuration;

/// <summary>Every section is bound and validated by the startup validator (what ValidateOnStart runs).</summary>
public class OptionsRegistrationTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rnrm-options");

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["ConnectionStrings:Default"] = "Server=db",
        ["Paths:Roms"] = Path.Combine(Root, "roms"),
        ["Paths:Ingest"] = Path.Combine(Root, "ingest"),
        ["Paths:Logs"] = Path.Combine(Root, "logs"),
        ["Paths:Cache"] = Path.Combine(Root, "cache"),
        ["Paths:Backups"] = Path.Combine(Root, "backups"),
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddAppOptions(config).BuildServiceProvider();
    }

    [Test]
    public void ValidSettings_PassAndBind()
    {
        var settings = ValidSettings();
        settings["Logging:RetentionDays"] = "30";
        settings["Sources:ThumbnailBaseUrl"] = "";
        using var services = Build(settings);

        Assert.DoesNotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        Assert.That(services.GetRequiredService<IOptions<LoggingOptions>>().Value.RetentionDays, Is.EqualTo(30));
        Assert.That(services.GetRequiredService<IOptions<SourcesOptions>>().Value.ThumbnailBaseUrl, Is.Empty);
        Assert.That(services.GetRequiredService<IOptions<IntegrityOptions>>().Value.QuickCheckTime, Is.EqualTo("03:00"));
    }

    [TestCase("Paths:Roms", "relative")]
    [TestCase("ConnectionStrings:Default", "")]
    [TestCase("Admin:InitialPassword", "short")]
    [TestCase("Integrity:QuickCheckTime", "3am")]
    [TestCase("Logging:MinLevel", "Verbose")]
    [TestCase("Logging:RetentionDays", "0")]
    [TestCase("Sources:DatBaseUrl", "ftp://x")]
    [TestCase("Sources:ThumbnailBaseUrl", "nope")]
    public void InvalidSetting_FailsStartupValidationNamingTheKey(string key, string value)
    {
        var settings = ValidSettings();
        settings[key] = value;
        using var services = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.That(ex!.Message, Does.Contain(key));
    }

    [Test]
    public void DefaultPaths_AreValidOnLinux()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "/app/... isn't fully qualified on Windows.");
        using var services = Build(new() { ["ConnectionStrings:Default"] = "Server=db" });

        Assert.DoesNotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }
}
