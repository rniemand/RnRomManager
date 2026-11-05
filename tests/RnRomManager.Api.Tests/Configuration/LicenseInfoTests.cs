using System.Text.Json;
using System.Xml.Linq;
using RnRomManager.Api.Configuration;

namespace RnRomManager.Api.Tests.Configuration;

/// <summary>The embedded <c>licenses.json</c> (spec §23 <c>/api/about</c>) lists the app, libretro-database and every direct dependency.</summary>
public class LicenseInfoTests
{
    private static readonly string[] Extra = ["RnRomManager", "libretro-database"];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RnRomManager.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Every NuGet package (central versions) and every npm dependency/devDependency.</summary>
    private static IEnumerable<string> Dependencies()
    {
        var root = RepoRoot();
        foreach (var package in XDocument.Load(Path.Combine(root, "Directory.Packages.props")).Descendants("PackageVersion"))
            yield return package.Attribute("Include")!.Value;
        using var npm = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "RnRomManager.Web", "package.json")));
        foreach (var section in new[] { "dependencies", "devDependencies" })
            foreach (var dependency in npm.RootElement.GetProperty(section).EnumerateObject())
                yield return dependency.Name;
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
