using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Tests.Users;

public class UserAdminServiceTests
{
    private const string NewPassword = "a-long-password-1";
    private static readonly AuditActor Actor = new(1, "root", null);
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly PasswordHasher<User> _hasher = new();
    private IUserRepository _users = null!;
    private IAuditLog _audit = null!;
    private UserAdminService _service = null!;
    private User _plain = null!;

    [SetUp]
    public void SetUp()
    {
        _users = Substitute.For<IUserRepository>();
        _audit = Substitute.For<IAuditLog>();
        _service = new UserAdminService(_users, _hasher, new FakeTimeProvider(Now), _audit, NullLogger<UserAdminService>.Instance);
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

        var result = await _service.CreateAsync("newbie", ["admin"], NewPassword, Actor, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.None));
        Assert.That(result.User!.Id, Is.EqualTo(42));
        Assert.That(result.User.Roles, Is.EqualTo(new[] { Roles.Admin }));
        Assert.That(result.User.MustChangePassword, Is.True);
        Assert.That(_hasher.VerifyHashedPassword(result.User, result.User.PasswordHash, NewPassword), Is.Not.EqualTo(PasswordVerificationResult.Failed));
    }

    [TestCase("x", "", NewPassword, "username")]
    [TestCase("newbie", "Admin", NewPassword, "roles")]
    [TestCase("newbie", "admin,admin", NewPassword, "roles")]
    [TestCase("newbie", "root", NewPassword, "roles")]
    [TestCase("newbie", "rom-manager", NewPassword, "roles")]
    [TestCase("newbie", "", "short", "password")]
    public async Task Create_Invalid_NamesTheField(string? username, string roles, string? password, string field)
    {
        var result = await _service.CreateAsync(username, roles.Split(',', StringSplitOptions.RemoveEmptyEntries), password, Actor, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.ValidationFailed));
        Assert.That(result.Errors!.Keys, Is.EqualTo(new[] { field }));
        await _users.DidNotReceiveWithAnyArgs().InsertAsync(default!, default);
    }

    [Test]
    public async Task Create_NullOrNullEntryRoles()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(42);

        Assert.That((await _service.CreateAsync("newbie", null, NewPassword, Actor, CancellationToken.None)).User!.Roles, Is.Empty);
        Assert.That((await _service.CreateAsync("newbie", [null], NewPassword, Actor, CancellationToken.None)).Errors!.Keys, Is.EqualTo(new[] { "roles" }));
    }

    [Test]
    public async Task Create_DuplicateName_IsUsernameTaken()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns<int>(_ => throw new UsernameTakenException("plain"));

        var result = await _service.CreateAsync("PLAIN", null, NewPassword, Actor, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.UsernameTaken));
    }

    [Test]
    public async Task Update_ChangesRolesAndRotatesTheStamp()
    {
        var result = await _service.UpdateAsync(7, ["admin"], null, Actor, CancellationToken.None);

        Assert.That(result.User!.Roles, Is.EqualTo(new[] { Roles.Admin }));
        Assert.That(result.User.SecurityStamp, Is.Not.EqualTo(_plain.SecurityStamp));
        Assert.That(result.User.UpdatedAt, Is.EqualTo(Now.UtcDateTime));
        await _users.Received(1).UpdateUnlessLastAdminAsync(result.User, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_NoChange_WritesNothing()
    {
        _plain = _plain with { Roles = Roles.Of(Roles.Admin) };

        var result = await _service.UpdateAsync(7, ["admin"], false, Actor, CancellationToken.None);

        Assert.That(result.User, Is.EqualTo(_plain));
        await _users.DidNotReceiveWithAnyArgs().UpdateUnlessLastAdminAsync(default!, default);
    }

    [Test]
    public async Task Update_RefusedByTheRepository_IsLastAdmin()
    {
        _users.UpdateUnlessLastAdminAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.UpdateAsync(7, null, true, Actor, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.LastAdmin));
    }

    [Test]
    public async Task Update_UnknownIdOrBadRole()
    {
        Assert.That((await _service.UpdateAsync(99, null, true, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.NotFound));
        Assert.That((await _service.UpdateAsync(7, ["root"], null, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.ValidationFailed));
    }

    [TestCase(null, true, TestName = "Update_Self_Disable_IsSelfChange")]
    [TestCase("", null, TestName = "Update_Self_Demote_IsSelfChange")]
    public async Task Update_Self_IsSelfChange(string? roles, bool? disabled)
    {
        _plain = _plain with { Id = 1, Roles = Roles.Of(Roles.Admin) };
        _users.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _plain);

        var result = await _service.UpdateAsync(1, roles?.Split(',', StringSplitOptions.RemoveEmptyEntries), disabled, Actor, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.SelfChange));
        await _users.DidNotReceiveWithAnyArgs().UpdateUnlessLastAdminAsync(default!, default);
    }

    [Test]
    public async Task Update_Self_NoChange_IsOk()
    {
        _plain = _plain with { Id = 1, Roles = Roles.Of(Roles.Admin) };
        _users.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _plain);

        var result = await _service.UpdateAsync(1, ["admin"], false, Actor, CancellationToken.None);

        Assert.That(result.Error, Is.EqualTo(UserAdminResult.Failure.None));
        await _users.DidNotReceiveWithAnyArgs().UpdateUnlessLastAdminAsync(default!, default);
    }

    [Test]
    public async Task Update_AnotherAdmin_DisableAndDemote_IsOk()
    {
        _plain = _plain with { Roles = Roles.Of(Roles.Admin) };

        Assert.That((await _service.UpdateAsync(7, null, true, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.None));
        Assert.That((await _service.UpdateAsync(7, [], null, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.None));
    }

    [Test]
    public async Task Update_Self_CheckOrder()
    {
        // NotFound before SelfChange: the actor's own id, but no such user.
        Assert.That((await _service.UpdateAsync(1, null, true, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.NotFound));

        // SelfChange before LastAdmin: the repository would refuse, but it's never asked.
        _plain = _plain with { Id = 1, Roles = Roles.Of(Roles.Admin) };
        _users.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _plain);
        _users.UpdateUnlessLastAdminAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(false);
        Assert.That((await _service.UpdateAsync(1, null, true, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.SelfChange));
    }

    [Test]
    public async Task ResetPassword_ClearsLockoutForcesChangeAndRotatesTheStamp()
    {
        _plain = _plain with { FailedLoginCount = 3, LockoutUntil = Now.UtcDateTime.AddMinutes(10) };

        var result = await _service.ResetPasswordAsync(7, NewPassword, Actor, CancellationToken.None);

        var user = result.User!;
        Assert.That((user.FailedLoginCount, user.LockoutUntil, user.MustChangePassword), Is.EqualTo((0, (DateTime?)null, true)));
        Assert.That(user.SecurityStamp, Is.Not.EqualTo(_plain.SecurityStamp));
        Assert.That(_hasher.VerifyHashedPassword(user, user.PasswordHash, NewPassword), Is.Not.EqualTo(PasswordVerificationResult.Failed));
        await _users.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResetPassword_PolicyFailureOrUnknownId()
    {
        Assert.That((await _service.ResetPasswordAsync(7, "short", Actor, CancellationToken.None)).Errors!.Keys, Is.EqualTo(new[] { "newPassword" }));
        Assert.That((await _service.ResetPasswordAsync(99, NewPassword, Actor, CancellationToken.None)).Error, Is.EqualTo(UserAdminResult.Failure.NotFound));
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

        var (user, password, promoted, reenabled) = (await _service.RecoverAsync("plain", CancellationToken.None))!.Value;

        Assert.That(password, Has.Length.EqualTo(20));
        Assert.That((user.IsDisabled, user.MustChangePassword, user.Roles.Count), Is.EqualTo((false, true, 0)));
        Assert.That(_hasher.VerifyHashedPassword(user, user.PasswordHash, password), Is.Not.EqualTo(PasswordVerificationResult.Failed));
        await _users.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
        Assert.That((promoted, reenabled), Is.EqualTo((false, true)));
    }

    [Test]
    public async Task Recover_SoleExistingAdmin_IsNotPromoted()
    {
        _plain = _plain with { Roles = Roles.Of(Roles.Admin) };
        _users.ListAsync(Arg.Any<CancellationToken>()).Returns([_plain]);

        var (_, _, promoted, reenabled) = (await _service.RecoverAsync("plain", CancellationToken.None))!.Value;

        Assert.That((promoted, reenabled), Is.EqualTo((false, false)));
    }

    [Test]
    public async Task Recover_PromotesWhenNoOtherAdminIsEnabled()
    {
        _users.ListAsync(Arg.Any<CancellationToken>()).Returns([_plain, NewUser(1, "admin", Roles.Of(Roles.Admin), disabled: true),
            NewUser(2, "site", Roles.None)]);

        var (user, _, promoted, _) = (await _service.RecoverAsync("plain", CancellationToken.None))!.Value;

        Assert.That(user.Roles, Is.EqualTo(new[] { Roles.Admin }), "a Site user isn't an admin");
        Assert.That(promoted, Is.True);
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    private IReadOnlyList<AuditEvent> Recorded() =>
        _audit.ReceivedCalls().Select(c => (AuditEvent)c.GetArguments()[0]!).ToList();

    [Test]
    public async Task Audit_Create_RecordsUserCreated()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(42);

        await _service.CreateAsync("newbie", ["admin"], NewPassword, Actor, CancellationToken.None);
        await _service.CreateAsync("visitor", null, NewPassword, Actor, CancellationToken.None);

        Assert.That(Recorded(), Is.EqualTo(new[]
        {
            new AuditEvent(AuditActions.UserCreated, Actor, Details: "user=newbie type=admin"),
            new AuditEvent(AuditActions.UserCreated, Actor, Details: "user=visitor type=site-user"),
        }));
    }

    [Test]
    public async Task Audit_TypeAndDisabledPatch_RecordsTwo_TypeFirst()
    {
        await _service.UpdateAsync(7, ["admin"], true, Actor, CancellationToken.None);

        Assert.That(Recorded(), Is.EqualTo(new[]
        {
            new AuditEvent(AuditActions.UserTypeChanged, Actor, Details: "user=plain from=site-user to=admin"),
            new AuditEvent(AuditActions.UserDisabled, Actor, Details: "user=plain"),
        }));
    }

    [Test]
    public async Task Audit_Enable_RecordsUserEnabled()
    {
        _plain = _plain with { IsDisabled = true };

        await _service.UpdateAsync(7, null, false, Actor, CancellationToken.None);

        Assert.That(Recorded(), Is.EqualTo(new[] { new AuditEvent(AuditActions.UserEnabled, Actor, Details: "user=plain") }));
    }

    [Test]
    public async Task Audit_ResetPassword_RecordsPasswordReset()
    {
        await _service.ResetPasswordAsync(7, NewPassword, Actor, CancellationToken.None);

        Assert.That(Recorded(), Is.EqualTo(new[] { new AuditEvent(AuditActions.PasswordReset, Actor, Details: "user=plain") }));
    }

    [Test]
    public async Task Audit_NoOpAndFailedOperations_RecordNothing()
    {
        _users.InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns<int>(_ => throw new UsernameTakenException("plain"));
        await _service.UpdateAsync(7, [], false, Actor, CancellationToken.None); // no-op
        await _service.CreateAsync("plain", null, NewPassword, Actor, CancellationToken.None); // UsernameTaken
        await _service.CreateAsync("x", null, NewPassword, Actor, CancellationToken.None); // invalid
        await _service.UpdateAsync(99, null, true, Actor, CancellationToken.None); // NotFound
        await _service.ResetPasswordAsync(7, "short", Actor, CancellationToken.None); // invalid

        var self = _plain with { Id = 1, Roles = Roles.Of(Roles.Admin) };
        _users.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(self);
        await _service.UpdateAsync(1, null, true, Actor, CancellationToken.None); // SelfChange

        _users.UpdateUnlessLastAdminAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(false);
        await _service.UpdateAsync(7, ["admin"], null, Actor, CancellationToken.None); // LastAdmin

        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }
}
