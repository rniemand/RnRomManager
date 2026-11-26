using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

public class RomStorageSettingsTests
{
    private ISettingsRepository _repo = null!;
    private Dictionary<string, string> _stored = null!;
    private RomStorageSettings _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _stored = new(StringComparer.Ordinal);
        _repo = Substitute.For<ISettingsRepository>();
        _repo.GetAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, string>)_stored
                .Where(v => call.Arg<IReadOnlyCollection<string>>().Contains(v.Key)).ToDictionary());
        var gb = TestHandlers.Fake("gb");
        var registry = new SystemRegistry([gb, new LegacyExternalTestHandler()]);
        _settings = new RomStorageSettings(_repo, () => registry, NullLogger<RomStorageSettings>.Instance);
    }

    [Test]
    public async Task Load_ReadsOnlyTheConfigurableSystemsKeys()
    {
        _stored["storage.embedRoms.legacy"] = "true";
        _stored["storage.embedRoms.gb"] = "false";

        await _settings.LoadAsync(CancellationToken.None);

        await _repo.Received(1).GetAsync(Arg.Is<IReadOnlyCollection<string>>(n => n.SequenceEqual(new[] { "storage.embedRoms.legacy" })),
            Arg.Any<CancellationToken>());
        Assert.That((_settings.Override("legacy"), _settings.Override("gb")), Is.EqualTo(((bool?)true, (bool?)null)));
    }

    [Test]
    public async Task Load_NoKey_IsNull_InvalidValue_IsIgnored()
    {
        await _settings.LoadAsync(CancellationToken.None);
        Assert.That(_settings.Override("legacy"), Is.Null);

        _stored["storage.embedRoms.legacy"] = "yes";
        await _settings.LoadAsync(CancellationToken.None);
        Assert.That(_settings.Override("legacy"), Is.Null);
    }

    [Test]
    public async Task LoadAgain_ReplacesTheValues()
    {
        _stored["storage.embedRoms.legacy"] = "true";
        await _settings.LoadAsync(CancellationToken.None);
        _stored.Clear();

        await _settings.LoadAsync(CancellationToken.None);

        Assert.That(_settings.Override("legacy"), Is.Null);
    }

    [Test]
    public async Task Set_WritesTheDatabase_ThenMemory()
    {
        await _settings.SetAsync("legacy", true, CancellationToken.None);

        await _repo.Received(1).SetAsync(Arg.Is<IReadOnlyDictionary<string, string>>(v => v.Count == 1 && v["storage.embedRoms.legacy"] == "true"),
            Arg.Any<CancellationToken>());
        Assert.That(_settings.Override("legacy"), Is.True);
    }

    [Test]
    public void Set_DatabaseFailure_LeavesMemory()
    {
        _repo.SetAsync(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _settings.SetAsync("legacy", true, CancellationToken.None));

        Assert.That(_settings.Override("legacy"), Is.Null);
    }

    [Test]
    public async Task Handler_FollowsTheSetting()
    {
        var handler = new LegacyExternalTestHandler(_settings);
        Assert.That(handler.EmbedRomsInZip, Is.False, "default");

        await _settings.SetAsync(handler.Id, true, CancellationToken.None);

        Assert.That((handler.EmbedRomsInZip, handler.DefaultEmbedRomsInZip), Is.EqualTo((true, false)));
    }

    [Test]
    public async Task LoadCompression_ReadsEverySystem_NoKeyOrInvalidIsMed()
    {
        _stored["storage.zipCompression.gb"] = "Store";
        _stored["storage.zipCompression.legacy"] = "High";
        await _settings.LoadAsync(CancellationToken.None);

        await _repo.Received(1).GetAsync(Arg.Is<IReadOnlyCollection<string>>(n => n.Order().SequenceEqual(new[] { "storage.zipCompression.gb", "storage.zipCompression.legacy" })),
            Arg.Any<CancellationToken>());
        Assert.That((_settings.Compression("gb"), _settings.Compression("legacy"), _settings.Compression("nes")),
            Is.EqualTo((ZipCompression.Store, ZipCompression.High, ZipCompression.Med)));

        foreach (var bad in new[] { "med", "2", "store", "" })
        {
            _stored["storage.zipCompression.gb"] = bad;
            await _settings.LoadAsync(CancellationToken.None); // no throw
            Assert.That(_settings.Compression("gb"), Is.EqualTo(ZipCompression.Med), bad);
        }
    }

    [Test]
    public async Task SetCompression_WritesTheDatabase_ThenMemory()
    {
        await _settings.SetCompressionAsync("gb", ZipCompression.Store, CancellationToken.None);

        await _repo.Received(1).SetAsync(Arg.Is<IReadOnlyDictionary<string, string>>(v => v.Count == 1 && v["storage.zipCompression.gb"] == "Store"),
            Arg.Any<CancellationToken>());
        Assert.That(_settings.Compression("gb"), Is.EqualTo(ZipCompression.Store));
    }

    [Test]
    public void SetCompression_DatabaseFailure_LeavesMemory()
    {
        _repo.SetAsync(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _settings.SetCompressionAsync("gb", ZipCompression.High, CancellationToken.None));

        Assert.That(_settings.Compression("gb"), Is.EqualTo(ZipCompression.Med));
    }
}
