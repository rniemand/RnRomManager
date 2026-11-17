using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Api.Tests.Integration;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Backups;

namespace RnRomManager.Api.Tests;

/// <summary>B27.4: the maintenance window a live restore opens.</summary>
public class MaintenanceGateTests
{
    private FakeTimeProvider _time = null!;
    private HttpContextAccessor _accessor = null!;
    private AuditWriter _writer = null!;
    private MaintenanceGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider();
        _accessor = new HttpContextAccessor();
        var repository = Substitute.For<IAuditRepository>();
        _writer = new AuditWriter(new AuditLog(repository, TimeProvider.System, Substitute.For<ILogger<AuditLog>>()), repository,
            NullLogger<AuditWriter>.Instance);
        _gate = new MaintenanceGate(_accessor, _writer, _time, NullLogger<MaintenanceGate>.Instance);
    }

    [TearDown]
    public void TearDown() => _writer.Dispose();

    private static DefaultHttpContext Context(string path, bool exempt = false)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (exempt)
            ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(MaintenanceExempt.Instance), "exempt"));
        return ctx;
    }

    /// <summary>A request that stays in flight until <paramref name="release"/> completes.</summary>
    private Task InFlight(DefaultHttpContext ctx, Task release) => _gate.InvokeAsync(ctx, _ => release);

    private async Task<int> StatusAsync(string path)
    {
        var ctx = Context(path);
        var reached = false;
        await _gate.InvokeAsync(ctx, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        return reached ? 0 : ctx.Response.StatusCode;
    }

    [Test]
    public async Task Closed_EveryRequestPassesThrough()
    {
        Assert.That(await StatusAsync("/api/games"), Is.Zero);
        Assert.That(await StatusAsync("/health/ready"), Is.Zero);
    }

    [Test]
    public async Task Open_OtherApiAndReadinessRequestsGet503Maintenance_WithRetryAfter()
    {
        await using var window = await _gate.TryEnterAsync(default);
        Assert.That(window, Is.Not.Null);

        var ctx = Context("/api/games");
        await _gate.InvokeAsync(ctx, _ => throw new AssertionException("must not run"));

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(ctx.Response.Headers.RetryAfter.ToString(), Is.EqualTo("5"));
        ctx.Response.Body.Position = 0;
        Assert.That(JsonDocument.Parse(ctx.Response.Body).RootElement.GetProperty("code").GetString(), Is.EqualTo("Maintenance"));
        Assert.That(await StatusAsync("/health/ready"), Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        Assert.That(await StatusAsync("/api/admin/logs/stream"), Is.EqualTo(StatusCodes.Status503ServiceUnavailable), "exempt streams are 503'd too");
        Assert.That(await StatusAsync("/health/live"), Is.Zero);
        Assert.That(await StatusAsync("/index.html"), Is.Zero);
    }

    [Test]
    public async Task Closing_ReopensTheApi_AndReleasesTheAuditHold()
    {
        var window = await _gate.TryEnterAsync(default);
        await window!.DisposeAsync();

        Assert.That(await StatusAsync("/api/games"), Is.Zero);
        Assert.That(_gate.IsOpen, Is.False);
        await _writer.HoldAsync(); // would wait forever if the gate's hold weren't released
        _writer.Release();
    }

    [Test]
    public async Task WaitsForAnInFlightRequest_ThenOpens()
    {
        var release = new TaskCompletionSource();
        var request = InFlight(Context("/api/games"), release.Task);

        var entering = _gate.TryEnterAsync(default);
        await Task.Delay(50);
        Assert.That(entering.IsCompleted, Is.False);
        Assert.That(await StatusAsync("/api/other"), Is.EqualTo(StatusCodes.Status503ServiceUnavailable), "new requests are refused while it drains");

        release.SetResult();
        await request;
        await using var window = await entering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(window, Is.Not.Null);
    }

    [Test]
    public async Task InFlightPastTheTimeout_IsRefused_AndTheGateCloses()
    {
        var release = new TaskCompletionSource();
        var request = InFlight(Context("/api/games"), release.Task);

        var entering = _gate.TryEnterAsync(default);
        await Task.Delay(50);
        _time.Advance(MaintenanceGate.DrainTimeout);

        Assert.That(await entering.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null);
        Assert.That(_gate.IsOpen, Is.False);
        Assert.That(await StatusAsync("/api/other"), Is.Zero);
        release.SetResult();
        await request;
    }

    [Test]
    public async Task AnInFlightExemptStream_DoesNotHoldItBack()
    {
        var release = new TaskCompletionSource();
        var stream = InFlight(Context("/api/games/1/roms/2/download", exempt: true), release.Task);

        await using var window = await _gate.TryEnterAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(window, Is.Not.Null);
        release.SetResult();
        await stream;
    }

    [Test]
    public async Task TheOwningRequestAloneInFlight_OpensAtOnce_AndIsNotRefused()
    {
        var owner = Context("/api/admin/backups/x/restore");
        IAsyncDisposable? window = null;
        var afterOpen = 0;
        await _gate.InvokeAsync(owner, async ctx =>
        {
            _accessor.HttpContext = ctx;
            window = await _gate.TryEnterAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            afterOpen = await StatusAsync("/api/games");
        });

        Assert.That(window, Is.Not.Null);
        Assert.That(owner.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), "the owner ran");
        Assert.That(afterOpen, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        await window!.DisposeAsync();
    }

    [Test]
    public async Task InTheHost_ItAnswersBeforeAuthentication()
    {
        using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient();
        var gate = factory.Services.GetRequiredService<IMaintenanceWindow>();

        await using (var window = await gate.TryEnterAsync(default))
        {
            using var response = await anonymous.GetAsync("/api/auth/me");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "not 401: the gate runs before auth");
            Assert.That(response.Headers.RetryAfter!.ToString(), Is.EqualTo("5"));
        }

        using var after = await anonymous.GetAsync("/api/auth/me");
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
