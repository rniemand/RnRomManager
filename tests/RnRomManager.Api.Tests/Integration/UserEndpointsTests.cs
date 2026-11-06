using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>/api/users*</c> and §9.1 (last admin, disable instead of delete), against MariaDB.</summary>
[Category("Integration")]
public class UserEndpointsTests
{
    private const string NewPassword = "a-long-password-1";

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private User _boss = null!;

    [SetUp]
    public async Task SetUp()
    {
        // The DB also holds the seeded 'admin' (an enabled Admin that must change its password).
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _admin = await AuthClient.CreateAsync(_factory);
        _boss = await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private IUserRepository Users => _factory.Services.GetRequiredService<IUserRepository>();

    private static IEnumerable<TestCaseData> Endpoints()
    {
        yield return new TestCaseData("GET", "/api/users", null).SetName("RoleMatrix GET users");
        yield return new TestCaseData("POST", "/api/users", new { username = "newbie", roles = Array.Empty<string>(), password = NewPassword }).SetName("RoleMatrix POST users");
        yield return new TestCaseData("PATCH", "/api/users/1", new { isDisabled = true }).SetName("RoleMatrix PATCH user");
        yield return new TestCaseData("POST", "/api/users/1/reset-password", new { newPassword = NewPassword }).SetName("RoleMatrix POST reset-password");
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task RoleMatrix_AnonymousIs401_UserIs403(string method, string url, object? body)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var anonymousResponse = await anonymous.SendAsync(new HttpMethod(method), url, body, sendXsrf: true);
        using var userResponse = await user.SendAsync(new HttpMethod(method), url, body, sendXsrf: true);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(userResponse), Is.EqualTo("Forbidden"));
    }

    [Test]
    public async Task List_ReturnsEveryUser()
    {
        using var response = await _admin.GetAsync("/api/users");
        var body = await AuthClient.JsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.EnumerateArray().Select(u => u.GetProperty("username").GetString()), Is.EqualTo(new[] { "admin", "boss" }));
        var boss = body.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "boss");
        Assert.That(boss.GetProperty("id").GetInt32(), Is.EqualTo(_boss.Id));
        Assert.That(boss.GetProperty("roles").EnumerateArray().Select(r => r.GetString()), Is.EqualTo(new[] { "admin", "rom-manager" }));
        Assert.That(boss.GetProperty("isDisabled").GetBoolean(), Is.False);
        Assert.That(boss.GetProperty("lastLoginAt").ValueKind, Is.EqualTo(JsonValueKind.String));
    }

    [Test]
    public async Task Create_Returns201_AndTheUserMustChangePasswordAtFirstLogin()
    {
        using var response = await _admin.PostAsync("/api/users", new { username = "newbie", roles = new[] { "rom-manager" }, password = NewPassword });
        var body = await AuthClient.JsonAsync(response);
        using var newbie = await AuthClient.CreateAsync(_factory);
        using var login = await newbie.LoginAsync("newbie", NewPassword);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(body.GetProperty("username").GetString(), Is.EqualTo("newbie"));
        Assert.That(body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()), Is.EqualTo(new[] { "rom-manager" }));
        Assert.That((await AuthClient.JsonAsync(login)).GetProperty("mustChangePassword").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Create_ExistingNameIgnoringCase_Is409UsernameTaken()
    {
        using var response = await _admin.PostAsync("/api/users", new { username = "BOSS", password = NewPassword });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("UsernameTaken"));
    }

    [Test]
    public async Task Create_Invalid_Is400WithFieldErrors()
    {
        using var response = await _admin.PostAsync("/api/users", new { username = "x", password = NewPassword });
        var body = await AuthClient.JsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(body.GetProperty("errors").TryGetProperty("username", out _), Is.True);
    }

    [Test]
    public async Task DisablingOrDemotingTheLastEnabledAdmin_Is409LastAdmin()
    {
        var seeded = await Users.GetByUsernameAsync("admin", CancellationToken.None);
        using var disableSeeded = await _admin.PatchAsync($"/api/users/{seeded!.Id}", new { isDisabled = true });

        using var demoteSelf = await _admin.PatchAsync($"/api/users/{_boss.Id}", new { roles = new[] { "rom-manager" } });
        using var disableSelf = await _admin.PatchAsync($"/api/users/{_boss.Id}", new { isDisabled = true });

        Assert.That(disableSeeded.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(disableSeeded)).GetProperty("isDisabled").GetBoolean(), Is.True);
        Assert.That(demoteSelf.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(demoteSelf), Is.EqualTo("LastAdmin"));
        Assert.That(await AuthClient.CodeAsync(disableSelf), Is.EqualTo("LastAdmin"));
        Assert.That((await Users.GetByIdAsync(_boss.Id, CancellationToken.None))!.Roles, Is.EquivalentTo(TestUsers.AllRoles));
    }

    [Test]
    public async Task RoleChange_KillsTheUsersSessions()
    {
        var plain = await TestUsers.CreateAsync(_factory, "plain");
        using var session = await AuthClient.CreateAsync(_factory);
        (await session.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var patch = await _admin.PatchAsync($"/api/users/{plain.Id}", new { roles = new[] { "admin" } });
        using var me = await session.GetAsync("/api/auth/me");

        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Create_WithoutRoles_HasNone()
    {
        using var response = await _admin.PostAsync("/api/users", new { username = "newbie", password = NewPassword });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("roles").GetArrayLength(), Is.Zero);
    }

    [TestCase("[\"Admin\"]")]
    [TestCase("[\"admin\",\"admin\"]")]
    [TestCase("[null]")]
    public async Task InvalidRoles_Is400OnTheRolesField(string roles)
    {
        var plain = await TestUsers.CreateAsync(_factory, "plain");
        var parsed = System.Text.Json.JsonSerializer.Deserialize<string?[]>(roles);

        using var create = await _admin.PostAsync("/api/users", new { username = "newbie", roles = parsed, password = NewPassword });
        using var patch = await _admin.PatchAsync($"/api/users/{plain.Id}", new { roles = parsed });

        foreach (var response in new[] { create, patch })
        {
            var body = await AuthClient.JsonAsync(response);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
            Assert.That(body.GetProperty("errors").GetProperty("roles")[0].GetString(), Is.EqualTo(Roles.ValidationError));
        }
    }

    [Test]
    public async Task Patch_WithoutRoles_KeepsThem()
    {
        var manager = await TestUsers.CreateAsync(_factory, "manager", Roles.Of(Roles.RomManager));

        using var patch = await _admin.PatchAsync($"/api/users/{manager.Id}", new { isDisabled = true });

        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Users.GetByIdAsync(manager.Id, CancellationToken.None))!.Roles, Is.EquivalentTo(new[] { Roles.RomManager }));
    }

    [Test]
    public async Task UnknownId_Is404()
    {
        using var patch = await _admin.PatchAsync("/api/users/9999", new { isDisabled = true });
        using var reset = await _admin.PostAsync("/api/users/9999/reset-password", new { newPassword = NewPassword });

        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ResetPassword_SetsMustChangeAndClearsTheLockout()
    {
        var plain = await TestUsers.CreateAsync(_factory, "plain");
        await Users.LockOutAsync(plain.Id, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow, CancellationToken.None);

        using var reset = await _admin.PostAsync($"/api/users/{plain.Id}/reset-password", new { newPassword = NewPassword });
        using var session = await AuthClient.CreateAsync(_factory);
        using var login = await session.LoginAsync("plain", NewPassword);

        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(login)).GetProperty("mustChangePassword").GetBoolean(), Is.True);
    }
}
