using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Starts the shared MariaDB container for the Integration tests in this namespace.</summary>
[SetUpFixture]
public sealed class MariaDbFixture
{
    [OneTimeSetUp]
    public Task StartAsync() => MariaDbServer.StartAsync();

    [OneTimeTearDown]
    public Task StopAsync() => MariaDbServer.StopAsync();
}
