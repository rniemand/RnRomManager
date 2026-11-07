using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B23: <c>/api/auth/me/hotkeys</c> against MariaDB.</summary>
[Category("Integration")]
public class HotkeyEndpointsTests
{
    private ApiFactory _factory = null!;
    private AuthClient _client = null!;
    private User _user = null!;

    private IUserRepository Users => _factory.Services.GetRequiredService<IUserRepository>();

    private async Task SignInAsync(IReadOnlySet<string>? roles = null, bool mustChangePassword = false)
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _client = await AuthClient.CreateAsync(_factory);
        _user = await TestUsers.CreateAsync(_factory, "keys", roles, mustChangePassword);
        (await _client.LoginAsync("keys", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private static async Task<Dictionary<string, string?>> BindingsAsync(HttpResponseMessage response)
    {
        var body = await AuthClient.JsonAsync(response);
        return body.GetProperty("bindings").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString());
    }

    private async Task<Dictionary<string, string?>> GetAsync()
    {
        using var response = await _client.GetAsync("/api/auth/me/hotkeys");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await BindingsAsync(response);
    }

    private Task<HttpResponseMessage> PutAsync(object bindings) =>
        _client.SendAsync(HttpMethod.Put, "/api/auth/me/hotkeys", new { bindings }, sendXsrf: true);

    private static readonly Dictionary<string, string?> Defaults = new()
    {
        ["nav.library"] = "Alt+Shift+L", ["nav.ingestion"] = "Alt+Shift+I", ["nav.review"] = "Alt+Shift+F", ["review.next"] = "Alt+Shift+G",
    };

    [Test]
    public async Task Get_ReturnsTheDefaultsForEveryEditableAction_EvenForAUser()
    {
        await SignInAsync();

        Assert.That(await GetAsync(), Is.EqualTo(Defaults));
    }

    [Test]
    public async Task Put_StoresOnlyNonDefaultOverrides_AndReturnsTheMergedResult()
    {
        await SignInAsync(TestUsers.AllRoles);

        using var response = await PutAsync(new Dictionary<string, string?>
        {
            ["nav.library"] = "Alt+Shift+L", ["nav.review"] = "shift+alt+r", ["review.next"] = null,
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var expected = new Dictionary<string, string?>(Defaults) { ["nav.review"] = "Alt+Shift+R", ["review.next"] = null };
        Assert.That(await BindingsAsync(response), Is.EqualTo(expected));
        Assert.That(await GetAsync(), Is.EqualTo(expected));
        Assert.That(await Users.GetHotkeysAsync(_user.Id, default), Is.EqualTo("""{"nav.review":"Alt+Shift+R","review.next":null}"""));
    }

    [Test]
    public async Task Put_Conflict_Is400KeyedByTheLaterAction_AndSavesNothing()
    {
        await SignInAsync(TestUsers.AllRoles);

        using var response = await PutAsync(new Dictionary<string, string?> { ["review.next"] = "Alt+Shift+I" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var errors = (await AuthClient.JsonAsync(response)).GetProperty("errors");
        Assert.That(errors.GetProperty("bindings.review.next")[0].GetString(), Is.EqualTo("Already used by Go to Ingestion."));
        Assert.That(await Users.GetHotkeysAsync(_user.Id, default), Is.Null);
    }

    [TestCase("Ctrl+K", Hotkeys.ReservedError)]
    [TestCase("Ctrl+Alt+L", Hotkeys.AltGrError)]
    [TestCase("Shift+L", Hotkeys.ModifierError)]
    [TestCase("Alt+Space", Hotkeys.FormatError)]
    public async Task Put_RuleViolation_Is400(string binding, string error)
    {
        await SignInAsync();

        using var response = await PutAsync(new Dictionary<string, string?> { ["nav.library"] = binding });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty("bindings.nav.library")[0].GetString(),
            Is.EqualTo(error));
    }

    [Test]
    public async Task Put_UnknownIdOrBadValueOrNoObject_Is400()
    {
        await SignInAsync();

        using var unknown = await PutAsync(new Dictionary<string, string?> { ["nav.nowhere"] = "Alt+Q" });
        using var number = await PutAsync(new Dictionary<string, object> { ["nav.library"] = 5 });
        using var missing = await _client.SendAsync(HttpMethod.Put, "/api/auth/me/hotkeys", new { }, sendXsrf: true);

        Assert.That((await AuthClient.JsonAsync(unknown)).GetProperty("errors").TryGetProperty("bindings.nav.nowhere", out _), Is.True);
        Assert.That((await AuthClient.JsonAsync(number)).GetProperty("errors").TryGetProperty("bindings.nav.library", out _), Is.True);
        Assert.That((await AuthClient.JsonAsync(missing)).GetProperty("errors").TryGetProperty("bindings", out _), Is.True);
    }

    [Test]
    public async Task Put_EmptyObject_ResetsToDefaults()
    {
        await SignInAsync();
        (await PutAsync(new Dictionary<string, string?> { ["nav.library"] = "Alt+Q" })).Dispose();

        using var response = await PutAsync(new Dictionary<string, string?>());

        Assert.That(await BindingsAsync(response), Is.EqualTo(Defaults));
        Assert.That(await Users.GetHotkeysAsync(_user.Id, default), Is.Null);
    }

    [Test]
    public async Task AUser_KeepsStoredAdminOverrides_AndMayTakeAnAdminActionsKey()
    {
        await SignInAsync();
        await Users.SetHotkeysAsync(_user.Id, """{"nav.review":"Alt+Shift+Z"}""", default);

        using var response = await PutAsync(new Dictionary<string, string?> { ["nav.library"] = "Alt+Shift+I", ["nav.review"] = "Alt+Q" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Users.GetHotkeysAsync(_user.Id, default),
            Is.EqualTo("""{"nav.library":"Alt+Shift+I","nav.review":"Alt+Shift+Z"}"""));
    }

    [Test]
    public async Task Get_ReturnsAStoredDuplicateUnchanged()
    {
        // e.g. the Admin role was given back after the user took an Admin action's key.
        await SignInAsync(TestUsers.AllRoles);
        await Users.SetHotkeysAsync(_user.Id, """{"nav.library":"Alt+Shift+I"}""", default);

        var bindings = await GetAsync();

        Assert.That((bindings["nav.library"], bindings["nav.ingestion"]), Is.EqualTo(("Alt+Shift+I", "Alt+Shift+I")));
    }

    [Test]
    public async Task MustChangePassword_BlocksBothEndpoints()
    {
        await SignInAsync(mustChangePassword: true);

        using var get = await _client.GetAsync("/api/auth/me/hotkeys");
        using var put = await PutAsync(new Dictionary<string, string?>());

        Assert.That((get.StatusCode, put.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
        Assert.That(await AuthClient.CodeAsync(get), Is.EqualTo("PasswordChangeRequired"));
    }
}
