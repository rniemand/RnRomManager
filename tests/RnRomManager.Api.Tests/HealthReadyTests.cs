using System.Net;
using NSubstitute;

namespace RnRomManager.Api.Tests;

public class HealthReadyTests
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task DbUpAndPathsExist_Returns200WithoutBody()
    {
        using var response = await _client.GetAsync("/health/ready");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [Test]
    public async Task DbDown_Returns503WithoutBody()
    {
        _factory.Database.PingAsync(Arg.Any<CancellationToken>()).Returns(false);

        using var response = await _client.GetAsync("/health/ready");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [TestCase("Paths:Roms")]
    [TestCase("Paths:Cache")]
    public async Task PathMissing_Returns503(string key)
    {
        Directory.Delete(_factory.Settings[key]!, recursive: true);

        using var response = await _client.GetAsync("/health/ready");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task HealthLive_IgnoresTheDatabase()
    {
        _factory.Database.PingAsync(Arg.Any<CancellationToken>()).Returns(false);

        using var response = await _client.GetAsync("/health/live");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public void Startup_WaitsForTheDatabaseThenMigrates()
    {
        Received.InOrder(() =>
        {
            _factory.Database.WaitUntilAvailableAsync(Arg.Any<CancellationToken>());
            _factory.Database.Migrate();
        });
    }
}
