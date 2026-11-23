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
        Assert.That(IgdbPlatforms.ById.Where(p => !snesFallbacks.Contains(p.Key)).Select(p => p.Value), Is.Unique);
        Assert.That(IgdbPlatforms.ById.Values, Is.All.Positive);
    }
}
