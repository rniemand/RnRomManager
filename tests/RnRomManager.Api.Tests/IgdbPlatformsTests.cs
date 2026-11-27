using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Tests;

/// <summary>IGDB suggestions S16: the platform map covers exactly the registered systems.</summary>
public class IgdbPlatformsTests
{
    [Test]
    public void Map_HasEveryRegisteredSystem_AndNothingElse()
    {
        var services = new ServiceCollection();
        AppHost.AddSystemHandlers(services);
        using var provider = services.BuildServiceProvider();
        var ids = provider.GetServices<ISystemHandler>().Select(h => h.Id).Order(StringComparer.Ordinal).ToList();

        Assert.That(IgdbPlatforms.ById.Keys.Order(StringComparer.Ordinal), Is.EqualTo(ids));
    }

    [Test]
    public void PlatformIds_AreDistinctAndPositive()
    {
        // Satellaview and Sufami Turbo search as SNES (19): their own IGDB platforms weren't looked up (no credentials).
        string[] snesFallbacks = ["satellaview", "sufami"];
        Assert.That(snesFallbacks.Select(id => IgdbPlatforms.ById[id]), Is.All.EqualTo(IgdbPlatforms.ById["snes"]));
        // Both arcade systems search IGDB's Arcade platform (52).
        Assert.That(IgdbPlatforms.ById["fbneo"], Is.EqualTo(IgdbPlatforms.ById["mame2003"]));
        // Commodore 128 searches IGDB's shared C64/128/MAX platform (15).
        Assert.That(IgdbPlatforms.ById["c128"], Is.EqualTo(IgdbPlatforms.ById["c64"]));
        Assert.That(IgdbPlatforms.ById["pc2"], Is.EqualTo(IgdbPlatforms.ById["ws"]));
        Assert.That(IgdbPlatforms.ById.Where(p => !snesFallbacks.Contains(p.Key) && p.Key != "fbneo" && p.Key != "c128" && p.Key != "pc2").Select(p => p.Value), Is.Unique);
        Assert.That(IgdbPlatforms.ById.Values, Is.All.Positive);
    }
}
