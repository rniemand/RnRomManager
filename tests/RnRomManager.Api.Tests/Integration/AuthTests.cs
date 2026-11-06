using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §9.1/§9.2 login, session, antiforgery, lockout and rate limit, end to end against MariaDB.</summary>
[Category("Integration")]
public class AuthTests
{
    private const string Password = TestUsers.Password;

    private FakeTimeProvider _time = null!;
    private ApiFactory _factory = null!;
    private AuthClient _client = null!;

    // A fresh host per test: the login rate limiter's window is real time.
    [SetUp]
    public async Task SetUp()
    {
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()) { Time = _time };
        _client = await AuthClient.CreateAsync(_factory);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private IUserRepository Users => _factory.Services.GetRequiredService<IUserRepository>();

    private Task<User> CreateUserAsync(string username = "richard", bool mustChangePassword = false) =>
        TestUsers.CreateAsync(_factory, username, mustChangePassword: mustChangePassword);

    [Test]
    public async Task Me_ListsTheRolesSorted()
    {
        await TestUsers.CreateAsync(_factory, "richard", Roles.Of(Roles.RomManager, Roles.Admin));
        (await _client.LoginAsync("richard", Password)).Dispose();

        using var me = await _client.GetAsync("/api/auth/me");
        var body = await AuthClient.JsonAsync(me);

        Assert.That(body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()), Is.EqualTo(new[] { "admin", "rom-manager" }));
    }

    [Test]
    public async Task Login_SetsTheSessionCookieAndReturnsTheUser()
    {
        var user = await CreateUserAsync();

        using var login = await _client.LoginAsync("RICHARD", Password);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("rnrm.auth=", StringComparison.Ordinal));
        using var me = await _client.GetAsync("/api/auth/me");
        var body = await AuthClient.JsonAsync(me);

        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(cookie, Does.Contain("httponly").IgnoreCase.And.Contain("samesite=strict").IgnoreCase.And.Contain("expires=").IgnoreCase);
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("id").GetInt32(), Is.EqualTo(user.Id));
        Assert.That(body.GetProperty("username").GetString(), Is.EqualTo("richard"));
        Assert.That(body.GetProperty("roles").GetArrayLength(), Is.Zero);
        Assert.That(body.GetProperty("mustChangePassword").GetBoolean(), Is.False);
        Assert.That((await Users.GetByIdAsync(user.Id, CancellationToken.None))!.LastLoginAt, Is.Not.Null);
    }

    [Test]
    public async Task WrongPasswordAndUnknownUser_GetTheSameGeneric401()
    {
        await CreateUserAsync();

        using var wrong = await _client.LoginAsync("richard", "wrong-password-x");
        using var unknown = await _client.LoginAsync("nobody", Password);

        Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await AuthClient.CodeAsync(wrong), Is.EqualTo("InvalidCredentials"));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await AuthClient.CodeAsync(unknown), Is.EqualTo("InvalidCredentials"));
    }

    [Test]
    public async Task FiveFailures_LockTheAccountFor15Minutes()
    {
        await CreateUserAsync();
        for (var i = 0; i < 5; i++)
            (await _client.LoginAsync("richard", "wrong-password-x")).Dispose();

        using var locked = await _client.LoginAsync("richard", Password);
        _time.Advance(TimeSpan.FromMinutes(15));
        using var unlocked = await _client.LoginAsync("richard", Password);

        Assert.That(locked.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await AuthClient.CodeAsync(locked), Is.EqualTo("InvalidCredentials"));
        Assert.That(unlocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task SuccessfulLogin_ResetsTheFailureCount()
    {
        await CreateUserAsync();
        for (var i = 0; i < 4; i++)
            (await _client.LoginAsync("richard", "wrong-password-x")).Dispose();
        (await _client.LoginAsync("richard", Password)).Dispose();
        for (var i = 0; i < 4; i++)
            (await _client.LoginAsync("richard", "wrong-password-x")).Dispose();

        using var login = await _client.LoginAsync("richard", Password);

        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task EleventhLoginInAMinute_Is429()
    {
        for (var i = 0; i < 10; i++)
            (await _client.LoginAsync("nobody", Password)).Dispose();

        using var limited = await _client.LoginAsync("nobody", Password);

        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await AuthClient.CodeAsync(limited), Is.EqualTo("TooManyRequests"));
    }

    [Test]
    public async Task MissingXsrfHeader_Is400()
    {
        await CreateUserAsync();

        using var login = await _client.LoginAsync("richard", Password, sendXsrf: false);

        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await AuthClient.CodeAsync(login), Is.EqualTo("AntiforgeryInvalid"));
    }

    [Test]
    public async Task Anonymous_Gets401ProblemInsteadOfARedirect()
    {
        using var me = await _client.GetAsync("/api/auth/me");

        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await AuthClient.CodeAsync(me), Is.EqualTo("Unauthorized"));
    }

    [Test]
    public async Task Logout_EndsTheSession()
    {
        await CreateUserAsync();
        (await _client.LoginAsync("richard", Password)).Dispose();

        using var logout = await _client.PostAsync("/api/auth/logout");
        using var me = await _client.GetAsync("/api/auth/me");

        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task MustChangePassword_BlocksEverythingButTheAllowList_UntilChanged()
    {
        await CreateUserAsync(mustChangePassword: true);
        (await _client.LoginAsync("richard", Password)).Dispose();

        using var other = await _client.GetAsync("/api/nope");
        using var me = await _client.GetAsync("/api/auth/me");
        using var antiforgery = await _client.GetAsync("/api/auth/antiforgery");
        using var change = await _client.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "a-brand-new-password" });
        using var afterChange = await _client.GetAsync("/api/nope");

        Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(other), Is.EqualTo("PasswordChangeRequired"));
        Assert.That((await AuthClient.JsonAsync(me)).GetProperty("mustChangePassword").GetBoolean(), Is.True);
        Assert.That(antiforgery.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(change.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(afterChange.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "gate lifted; unknown route is a plain 404");
    }

    [Test]
    public async Task ChangePassword_KillsOtherSessionsButKeepsTheCurrentOne()
    {
        await CreateUserAsync();
        using var other = await AuthClient.CreateAsync(_factory);
        (await _client.LoginAsync("richard", Password)).Dispose();
        (await other.LoginAsync("richard", Password)).Dispose();

        using var change = await _client.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "a-brand-new-password" });
        using var current = await _client.GetAsync("/api/auth/me");
        using var stale = await other.GetAsync("/api/auth/me");
        using var logout = await _client.PostAsync("/api/auth/logout");

        Assert.That(change.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(current.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "the re-issued XSRF token matches the re-signed session");
    }

    [Test]
    public async Task ChangePassword_WrongCurrent_IsValidationFailed()
    {
        await CreateUserAsync();
        (await _client.LoginAsync("richard", Password)).Dispose();

        using var change = await _client.PostAsync("/api/auth/change-password", new { currentPassword = "wrong-password-x", newPassword = "a-brand-new-password" });
        var body = await AuthClient.JsonAsync(change);

        Assert.That(change.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(body.GetProperty("errors").TryGetProperty("currentPassword", out _), Is.True);
    }

    [Test]
    public async Task DisablingAUser_KillsTheirSession()
    {
        var user = await CreateUserAsync();
        (await _client.LoginAsync("richard", Password)).Dispose();

        await Users.UpdateAsync(user with { IsDisabled = true }, CancellationToken.None);
        using var me = await _client.GetAsync("/api/auth/me");

        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
