using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Tests.Users;

public class UserAdminServiceTests
{
    private const string NewPassword = "a-long-password-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly PasswordHasher<User> _hasher = new();
    private IUserRepository _users = null!;
    private UserAdminService _service = null!;
    private User _plain = null!;

    [SetUp]
    public void SetUp()
    {
        _users = Substitute.For<IUserRepository>();
        _service = new UserAdminService(_users, _hasher, new FakeTimeProvider(Now), NullLogger<UserAdminService>.Instance);
        _plain = NewUser(7, "plain", Roles.None);
        _users.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(_ => _plain);
        _users.GetByUsernameAsync("plain", Arg.Any<CancellationToken>()).Returns(_ => _plain);
        _users.UpdateUnlessLastAdminAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static User NewUser(int id, string username, IReadOnlySet<string> roles, bool disabled = false) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "old-hash",
        Roles = roles,
        IsDisabled = disabled,
        SecurityStamp = new string('a', 32),
        CreatedAt = Now.UtcDateTime.AddDays(-1),
        UpdatedAt = Now.UtcDateTime.AddDays(-1),
    };

    [Test]
    public async Task Create_InsertsAUserThatMustChangePassword()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(42);

        var result = await _service.CreateAsync("newbie", ["rom-manager", "admin"], NewPassword, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.None));
        Assert.That(result.User!.Id, Is.EqualTo(42));
        Assert.That(result.User.Roles, Is.EqualTo(new[] { Roles.Admin, Roles.RomManager }), "sorted");
        Assert.That(result.User.MustChangePassword, Is.True);
        Assert.That(_hasher.VerifyHashedPassword(result.User, result.User.PasswordHash, NewPassword), Is.Not.EqualTo(PasswordVerificationResult.Failed));
    }

    [TestCase("x", "", NewPassword, "username")]
    [TestCase("newbie", "Admin", NewPassword, "roles")]
    [TestCase("newbie", "admin,admin", NewPassword, "roles")]
    [TestCase("newbie", "root", NewPassword, "roles")]
    [TestCase("newbie", "", "short", "password")]
    public async Task Create_Invalid_NamesTheField(string? username, string roles, string? password, string field)
    {
        var result = await _service.CreateAsync(username, roles.Split(',', StringSplitOptions.RemoveEmptyEntries), password, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.ValidationFailed));
        Assert.That(result.Errors!.Keys, Is.EqualTo(new[] { field }));
        await _users.DidNotReceiveWithAnyArgs().InsertAsync(default!, default);
    }

    [Test]
    public async Task Create_NullOrNullEntryRoles()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(42);

        Assert.That((await _service.CreateAsync("newbie", null, NewPassword, CancellationToken.None)).User!.Roles, Is.Empty);
        Assert.That((await _service.CreateAsync("newbie", [null], NewPassword, CancellationToken.None)).Errors!.Keys, Is.EqualTo(new[] { "roles" }));
    }

    [Test]
    public async Task Create_DuplicateName_IsUsernameTaken()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns<int>(_ => throw new UsernameTakenException("plain"));

        var result = await _service.CreateAsync("PLAIN", null, NewPassword, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.UsernameTaken));
    }

    [Test]
    public async Task Update_ChangesRolesAndRotatesTheStamp()
    {
        var result = await _service.UpdateAsync(7, ["rom-manager"], null, CancellationToken.None);

        Assert.That(result.User!.Roles, Is.EqualTo(new[] { Roles.RomManager }));
        Assert.That(result.User.SecurityStamp, Is.Not.EqualTo(_plain.SecurityStamp));
        Assert.That(result.User.UpdatedAt, Is.EqualTo(Now.UtcDateTime));
        await _users.Received(1).UpdateUnlessLastAdminAsync(result.User, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_NoChange_WritesNothing()
    {
        _plain = _plain with { Roles = Roles.Of(Roles.Admin) };

        var result = await _service.UpdateAsync(7, ["admin"], false, CancellationToken.None);

        Assert.That(result.User, Is.EqualTo(_plain));
        await _users.DidNotReceiveWithAnyArgs().UpdateUnlessLastAdminAsync(default!, default);
    }

    [Test]
    public async Task Update_RefusedByTheRepository_IsLastAdmin()
    {
        _users.UpdateUnlessLastAdminAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.UpdateAsync(7, null, true, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.LastAdmin));
    }

    [Test]
    public async Task Update_UnknownIdOrBadRole()
    {
        Assert.That((await _service.UpdateAsync(99, null, true, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.NotFound));
        Assert.That((await _service.UpdateAsync(7, ["root"], null, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.ValidationFailed));
    }

    [Test]
    public async Task ResetPassword_ClearsLockoutForcesChangeAndRotatesTheStamp()
    {
        _plain = _plain with { FailedLoginCount = 3, LockoutUntil = Now.UtcDateTime.AddMinutes(10) };

        var result = await _service.ResetPasswordAsync(7, NewPassword, CancellationToken.None);

        var user = result.User!;
        Assert.That((user.FailedLoginCount, user.LockoutUntil, user.MustChangePassword), Is.EqualTo((0, (DateTime?)null, true)));
        Assert.That(user.SecurityStamp, Is.Not.EqualTo(_plain.SecurityStamp));
        Assert.That(_hasher.VerifyHashedPassword(user, user.PasswordHash, NewPassword), Is.Not.EqualTo(PasswordVerificationResult.Failed));
        await _users.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResetPassword_PolicyFailureOrUnknownId()
    {
        Assert.That((await _service.ResetPasswordAsync(7, "short", CancellationToken.None)).Errors!.Keys, Is.EqualTo(new[] { "newPassword" }));
        Assert.That((await _service.ResetPasswordAsync(99, NewPassword, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.NotFound));
    }

    [Test]
    public async Task Recover_UnknownUser_IsNull()
    {
        Assert.That(await _service.RecoverAsync("nobody", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Recover_ReEnablesWithARandomPassword_KeepsRoleWhenAnotherAdminIsEnabled()
    {
        _plain = _plain with { IsDisabled = true };
        _users.ListAsync(Arg.Any<CancellationToken>()).Returns([_plain, NewUser(1, "admin", Roles.Of(Roles.Admin))]);

        var (user, password) = (await _service.RecoverAsync("plain", CancellationToken.None))!.Value;

        Assert.That(password, Has.Length.EqualTo(20));
        Assert.That((user.IsDisabled, user.MustChangePassword, user.Roles.Count), Is.EqualTo((false, true, 0)));
        Assert.That(_hasher.VerifyHashedPassword(user, user.PasswordHash, password), Is.Not.EqualTo(PasswordVerificationResult.Failed));
        await _users.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Recover_PromotesWhenNoOtherAdminIsEnabled()
    {
        _users.ListAsync(Arg.Any<CancellationToken>()).Returns([_plain, NewUser(1, "admin", Roles.Of(Roles.Admin), disabled: true),
            NewUser(2, "manager", Roles.Of(Roles.RomManager))]);

        var (user, _) = (await _service.RecoverAsync("plain", CancellationToken.None))!.Value;

        Assert.That(user.Roles, Is.EqualTo(new[] { Roles.Admin, Roles.RomManager }), "a rom-manager isn't an admin");
    }
}
