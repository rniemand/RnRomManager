using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

[Category("Integration")]
public class AuditEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private static readonly DateTime Start = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly Guid _game = Guid.NewGuid();

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var repository = _factory.Services.GetRequiredService<IAuditRepository>();
        await repository.InsertAsync(new AuditEvent(AuditActions.Ingested, new AuditActor(1, "André", null), "gb", _game,
            "ingest/100%_rom.gb", "roms/a.zip", "alpha") { OccurredAt = Start }, default);
        await repository.InsertAsync(new AuditEvent(AuditActions.DatsRefreshed, new AuditActor(null, null, null), Details: "beta")
            { OccurredAt = Start.AddHours(1) }, default);
        await repository.InsertAsync(new AuditEvent(AuditActions.GameUpdated, new AuditActor(2, "other", null), "gbc",
            Path: "roms/c.zip", Details: "gamma") { OccurredAt = Start.AddHours(1) }, default);
    }

    [TearDown]
    public void TearDown() { _admin.Dispose(); _factory.Dispose(); }

    private async Task<JsonElement> Get(string query = "")
    {
        using var response = await _admin.GetAsync("/api/admin/audit" + query);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await AuthClient.JsonAsync(response);
    }

    [Test]
    public async Task DefaultOrderPagingAndColumns()
    {
        var result = await Get();
        Assert.That(result.GetProperty("pageSize").GetInt32(), Is.EqualTo(100));
        Assert.That(result.GetProperty("total").GetInt64(), Is.EqualTo(3));
        Assert.That(result.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("action").GetString()),
            Is.EqualTo(new[] { "GameUpdated", "DatsRefreshed", "Ingested" }));
        var item = (await Get("?page=3&pageSize=1")).GetProperty("items")[0];
        Assert.That(item.GetProperty("gameId").GetGuid(), Is.EqualTo(_game));
        Assert.That(item.GetProperty("user").GetString(), Is.EqualTo("André"));
        Assert.That(item.GetProperty("occurredAt").GetString(), Does.EndWith("Z"));
    }

    [TestCase("action=Ingested", "Ingested")]
    [TestCase("system=gb", "Ingested")]
    [TestCase("user=ANDRE", "Ingested")]
    [TestCase("scheduled=true", "DatsRefreshed")]
    [TestCase("q=100%25_", "Ingested")]
    [TestCase("q=a.zip", "Ingested")]
    [TestCase("q=beta", "DatsRefreshed")]
    [TestCase("from=2026-11-06T12:00:00Z&to=2026-11-06T13:00:00Z", "Ingested")]
    [TestCase("from=2026-11-06T14:00:00%2B02:00&to=2026-11-06T15:00:00%2B02:00", "Ingested")]
    [TestCase("action=Ingested&system=gb&user=andre&q=alpha&from=2026-11-06T12:00:00Z&to=2026-11-06T13:00:00Z", "Ingested")]
    public async Task Filters(string query, string action)
    {
        var items = (await Get("?" + query)).GetProperty("items");
        Assert.That(items.GetArrayLength(), Is.EqualTo(1));
        Assert.That(items[0].GetProperty("action").GetString(), Is.EqualTo(action));
    }

    [Test]
    public async Task BlankFiltersAndActions()
    {
        Assert.That((await Get("?user=%20&q=%20")).GetProperty("total").GetInt64(), Is.EqualTo(3));
        using var response = await _admin.GetAsync("/api/admin/audit/actions");
        Assert.That((await AuthClient.JsonAsync(response)).EnumerateArray().Select(x => x.GetString()), Is.EqualTo(AuditActions.All));
    }

    [TestCase("scheduled=true&user=boss")]
    [TestCase("from=nope")]
    [TestCase("from=2026-11-06T12:00:00")]
    [TestCase("from=2026-11-06T13:00:00Z&to=2026-11-06T12:00:00Z")]
    [TestCase("from=2026-11-06T12:00:00Z&to=2026-11-06T12:00:00Z")]
    [TestCase("action=Unknown")]
    [TestCase("system=unknown")]
    [TestCase("pageSize=201")]
    [TestCase("page=0")]
    public async Task InvalidFilters(string query)
    {
        using var response = await _admin.GetAsync("/api/admin/audit?" + query);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("ValidationFailed"));
    }

    [Test]
    public Task LongSearchIsRejected() => InvalidFilters("q=" + new string('x', 201));
}
