using RnRomManager.Api.Cli;

namespace RnRomManager.Api.Tests.Cli;

public class HealthCheckCommandTests
{
    [TestCase(null, 8080)]
    [TestCase("", 8080)]
    [TestCase("  ", 8080)]
    [TestCase("5080", 5080)]
    [TestCase(" 5080 ", 5080)]
    [TestCase("8081;8082", 8081)]
    [TestCase(";9000", 9000)]
    [TestCase("abc", 8080)]
    [TestCase("0", 8080)]
    [TestCase("70000", 8080)]
    public void PortFrom_UsesFirstValidEntryOrDefault(string? httpPorts, int expected) =>
        Assert.That(HealthCheckCommand.PortFrom(httpPorts), Is.EqualTo(expected));

    [Test]
    public async Task RunAsync_NothingListening_Returns1()
    {
        // Port 1 on loopback: nothing listens there, so the connection is refused.
        Assert.That(await HealthCheckCommand.RunAsync("1"), Is.EqualTo(1));
    }
}
