using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Tests;

/// <summary>No DB needed: nothing listens on 127.0.0.1:1, so every connect is refused.</summary>
public class MySqlDatabaseLifecycleTests
{
    private const string Unreachable = "Server=127.0.0.1;Port=1;Database=rnrommanager;User ID=app;Password=pw;Connection Timeout=1";

    private static MySqlDatabaseLifecycle Lifecycle(TimeSpan timeout) => new(
        new MySqlConnectionFactory(Unreachable), NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System)
    {
        WaitTimeout = timeout,
        RetryInterval = TimeSpan.FromMilliseconds(100),
    };

    [Test]
    public async Task WaitUntilAvailable_Unreachable_GivesUpAfterTheTimeout()
    {
        var timeout = TimeSpan.FromSeconds(1);
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => Lifecycle(timeout).WaitUntilAvailableAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.StartWith("Database 'rnrommanager' was not reachable within 1 s"));
        Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(timeout - TimeSpan.FromMilliseconds(50)));
        Assert.That(watch.Elapsed, Is.LessThan(timeout + TimeSpan.FromSeconds(3)), "must not overrun the wait");
    }

    [Test]
    public async Task WaitUntilAvailable_Cancelled_StopsWithCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.CatchAsync<OperationCanceledException>(() => Lifecycle(TimeSpan.FromSeconds(30)).WaitUntilAvailableAsync(cts.Token));
    }

    [Test]
    public async Task Ping_Unreachable_ReturnsFalseWithoutThrowing() =>
        Assert.That(await Lifecycle(TimeSpan.FromSeconds(1)).PingAsync(CancellationToken.None), Is.False);
}
