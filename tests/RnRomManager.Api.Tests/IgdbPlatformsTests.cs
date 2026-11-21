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
        Assert.That(IgdbPlatforms.ById.Values, Is.Unique);
        Assert.That(IgdbPlatforms.ById.Values, Is.All.Positive);
    }
}
