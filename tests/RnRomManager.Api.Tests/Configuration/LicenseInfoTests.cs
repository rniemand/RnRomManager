using System.Text.Json;
using System.Xml.Linq;
using RnRomManager.Api.Configuration;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Configuration;

/// <summary>The embedded <c>licenses.json</c> (spec §23 <c>/api/about</c>) lists the app, libretro-database and every direct dependency.</summary>
public class LicenseInfoTests
{
    private static readonly string[] Extra = ["RnRomManager", "libretro-database"];


    /// <summary>Every NuGet package (central versions), every npm dependency/devDependency and the <c>emulatorjs-next.json</c> pins.</summary>
    private static IEnumerable<string> Dependencies()
    {
        var root = RepoPaths.Root();
        foreach (var package in XDocument.Load(Path.Combine(root, "Directory.Packages.props")).Descendants("PackageVersion"))
            yield return package.Attribute("Include")!.Value;
        using var npm = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "RnRomManager.Web", "package.json")));
        foreach (var section in new[] { "dependencies", "devDependencies" })
            foreach (var dependency in npm.RootElement.GetProperty(section).EnumerateObject())
                yield return dependency.Name;
        // 3DS V4: the side-by-side EmulatorJS 4.3.0-pre and its azahar core are pinned downloads, not npm packages.
        using var next = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "RnRomManager.Web", "emulatorjs-next.json")));
        foreach (var entry in new[] { "source", "core" })
            yield return next.RootElement.GetProperty(entry).GetProperty("name").GetString()!;
    }

    [Test]
    public void ListsExactlyTheDirectDependencies_PlusTheAppAndLibretro()
    {
        var listed = LicenseInfo.All.Select(l => l.Name).ToList();

        Assert.That(listed, Is.Unique);
        Assert.That(Dependencies().Except(listed), Is.Empty, "dependencies missing from licenses.json");
        Assert.That(listed.Except(Dependencies()).Except(Extra), Is.Empty, "licenses.json entries that are no longer dependencies");
    }

    [Test]
    public void EveryEntry_HasALicenceAndAnHttpsUrl()
    {
        Assert.That(LicenseInfo.All, Is.Not.Empty);
        foreach (var license in LicenseInfo.All)
        {
            Assert.That(license.License, Is.Not.Empty, license.Name);
            Assert.That(license.Url, Does.StartWith("https://"), license.Name);
        }
    }
}
