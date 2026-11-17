using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>B28: the stored DAT refresh schedule and its service.</summary>
public class DatRefreshSettingsTests
{
    [Test]
    public void NothingStored_IsOnAt0400()
    {
        Assert.That(DatRefreshSettings.FromStored(new Dictionary<string, string>()), Is.EqualTo(new DatRefreshSettings(true, "04:00")));
    }

    [Test]
    public void StoredRows_OverrideTheDefaults_AndRoundTrip()
    {
        var value = new DatRefreshSettings(false, "23:59");

        Assert.That(DatRefreshSettings.FromStored(value.ToStored()), Is.EqualTo(value));
        Assert.That(value.ToStored(), Is.EqualTo(new Dictionary<string, string>
        {
            ["dats.autoRefresh.enabled"] = "false",
            ["dats.autoRefresh.time"] = "23:59",
        }));
    }

    [TestCase("maybe", "25:00")]
    [TestCase("", "4:00")]
    [TestCase("1", "04:0")]
    public void InvalidStoredRows_FallBackToTheirDefault(string enabled, string time)
    {
        var stored = new Dictionary<string, string> { [DatRefreshSettings.EnabledKey] = enabled, [DatRefreshSettings.TimeKey] = time };

        Assert.That(DatRefreshSettings.FromStored(stored), Is.EqualTo(DatRefreshSettings.Defaults));
    }

    private static (DatRefreshSettingsService Service, ISettingsRepository Repository) Service(DateTimeOffset now)
    {
        var repository = Substitute.For<ISettingsRepository>();
        return (new DatRefreshSettingsService(repository, TimeZoneInfo.Utc, new FakeTimeProvider(now), NullLogger<DatRefreshSettingsService>.Instance), repository);
    }

    [Test]
    public async Task Save_StoresBothKeys_AndRaisesSettingsChanged()
    {
        var (service, repository) = Service(DateTimeOffset.UnixEpoch);
        var raised = 0;
        service.SettingsChanged += () => raised++;

        await service.SaveAsync(new DatRefreshSettings(false, "05:15"), default);

        await repository.Received(1).SetAsync(Arg.Is<IReadOnlyDictionary<string, string>>(d =>
            d[DatRefreshSettings.EnabledKey] == "false" && d[DatRefreshSettings.TimeKey] == "05:15"), Arg.Any<CancellationToken>());
        Assert.That(raised, Is.EqualTo(1));
        service.NotifySettingsChanged();
        Assert.That(raised, Is.EqualTo(2));
    }

    [Test]
    public void Save_InvalidTime_ThrowsAndStoresNothing()
    {
        var (service, repository) = Service(DateTimeOffset.UnixEpoch);

        Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new DatRefreshSettings(true, "24:00"), default));
        repository.DidNotReceiveWithAnyArgs().SetAsync(default!, default);
    }

    [Test]
    public void NextRunAt_IsTheNextDailyTime_OrNullWhenDisabled()
    {
        var (service, _) = Service(new DateTimeOffset(2026, 11, 5, 5, 0, 0, TimeSpan.Zero));

        Assert.That(service.NextRunAt(new DatRefreshSettings(true, "04:00")), Is.EqualTo(new DateTimeOffset(2026, 11, 6, 4, 0, 0, TimeSpan.Zero)));
        Assert.That(service.NextRunAt(new DatRefreshSettings(false, "04:00")), Is.Null);
    }
}
