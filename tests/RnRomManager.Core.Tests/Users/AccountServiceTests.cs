using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Tests.Users;

public class AccountServiceTests
{
    private const string Password = "correct-horse-battery";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly PasswordHasher<User> _hasher = new();
    private IUserRepository _users = null!;
    private FakeTimeProvider _time = null!;
    private AccountService _service = null!;
    private User _user = null!;

    [SetUp]
    public void SetUp()
    {
        _users = Substitute.For<IUserRepository>();
        _time = new FakeTimeProvider(Now);
        _service = new AccountService(_users, _hasher, _time, NullLogger<AccountService>.Instance);
        _user = new User
        {
            Id = 7,
            Username = "richard",
            PasswordHash = "",
            SecurityStamp = new string('a', 32),
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
        };
        _user = _user with { PasswordHash = _hasher.HashPassword(_user, Password) };
        _users.GetByUsernameAsync("richard", Arg.Any<CancellationToken>()).Returns(_ => _user);
    }

    private Task<User?> Login(string password = Password) => _service.LoginAsync("richard", password, CancellationToken.None);

    [Test]
    public async Task CorrectPassword_ReturnsUserAndRecordsLogin()
    {
        var user = await Login();

        Assert.That(user?.LastLoginAt, Is.EqualTo(Now.UtcDateTime));
        await _users.Received(1).RecordSuccessfulLoginAsync(7, Now.UtcDateTime, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownUser_ReturnsNull()
    {
        Assert.That(await _service.LoginAsync("nobody", Password, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task WrongPassword_CountsTheFailure_WithoutLockingBeforeTheFifth()
    {
        _users.IncrementFailedLoginCountAsync(7, Arg.Any<CancellationToken>()).Returns(4);

        Assert.That(await Login("wrong-password-x"), Is.Null);
        await _users.DidNotReceiveWithAnyArgs().LockOutAsync(default, default, default, default);
    }

    [Test]
    public async Task FifthFailure_LocksFor15Minutes()
    {
        _users.IncrementFailedLoginCountAsync(7, Arg.Any<CancellationToken>()).Returns(5);

        Assert.That(await Login("wrong-password-x"), Is.Null);
        await _users.Received(1).LockOutAsync(7, Now.UtcDateTime.AddMinutes(15), Now.UtcDateTime, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LockedOut_RejectsTheCorrectPasswordWithoutCountingIt()
    {
        _user = _user with { LockoutUntil = Now.UtcDateTime.AddMinutes(1) };

        Assert.That(await Login(), Is.Null);
        await _users.DidNotReceiveWithAnyArgs().IncrementFailedLoginCountAsync(default, default);
        await _users.DidNotReceiveWithAnyArgs().RecordSuccessfulLoginAsync(default, default, default);
    }

    [Test]
    public async Task LockoutExpired_AllowsLogin()
    {
        _user = _user with { LockoutUntil = Now.UtcDateTime };

        Assert.That(await Login(), Is.Not.Null);
    }

    [Test]
    public async Task Disabled_RejectsTheCorrectPassword()
    {
        _user = _user with { IsDisabled = true };

        Assert.That(await Login(), Is.Null);
    }

    [Test]
    public async Task ChangePassword_HashesRotatesStampAndClearsMustChange()
    {
        _user = _user with { MustChangePassword = true };
        _time.Advance(TimeSpan.FromMinutes(5));

        var (updated, errors) = await _service.ChangePasswordAsync(_user, Password, "a-brand-new-password", CancellationToken.None);

        Assert.That(errors, Is.Null);
        Assert.That(updated!.MustChangePassword, Is.False);
        Assert.That(updated.SecurityStamp, Is.Not.EqualTo(_user.SecurityStamp).And.Match("^[0-9a-f]{32}$"));
        Assert.That(updated.UpdatedAt, Is.EqualTo(Now.UtcDateTime.AddMinutes(5)));
        Assert.That(_hasher.VerifyHashedPassword(updated, updated.PasswordHash, "a-brand-new-password"), Is.Not.EqualTo(PasswordVerificationResult.Failed));
        await _users.Received(1).UpdateAsync(updated, Arg.Any<CancellationToken>());
    }

    [TestCase(null)]
    [TestCase("wrong-password-x")]
    public async Task ChangePassword_WrongCurrent_IsAFieldError(string? current)
    {
        var (updated, errors) = await _service.ChangePasswordAsync(_user, current, "a-brand-new-password", CancellationToken.None);

        Assert.That(updated, Is.Null);
        Assert.That(errors!.Keys, Is.EqualTo(new[] { "currentPassword" }));
        await _users.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [TestCase("short")]
    [TestCase(null)]
    public async Task ChangePassword_PolicyFailure_IsAFieldError(string? newPassword)
    {
        var (_, errors) = await _service.ChangePasswordAsync(_user, Password, newPassword, CancellationToken.None);

        Assert.That(errors!.Keys, Is.EqualTo(new[] { "newPassword" }));
    }
}
