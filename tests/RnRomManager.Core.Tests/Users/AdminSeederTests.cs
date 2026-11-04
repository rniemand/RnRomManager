using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Tests.Users;

public class AdminSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private IUserRepository _users = null!;
    private readonly PasswordHasher<User> _hasher = new();
    private StringWriter _console = null!;
    private User? _inserted;

    [SetUp]
    public void SetUp()
    {
        _users = Substitute.For<IUserRepository>();
        _users.InsertAsync(Arg.Do<User>(u => _inserted = u), Arg.Any<CancellationToken>()).Returns(1);
        _console = new StringWriter();
        _inserted = null;
    }

    [TearDown]
    public void TearDown() => _console.Dispose();

    private AdminSeeder Seeder(string initialPassword = "") => new(
        _users, _hasher, Options.Create(new AdminOptions { InitialPassword = initialPassword }),
        new FakeTimeProvider(Now), NullLogger<AdminSeeder>.Instance)
    {
        Console = _console,
    };

    [Test]
    public async Task UsersExist_DoesNothing()
    {
        _users.CountAsync(Arg.Any<CancellationToken>()).Returns(1);

        Assert.That(await Seeder().SeedAsync(CancellationToken.None), Is.False);
        await _users.DidNotReceive().InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        Assert.That(_console.ToString(), Is.Empty);
    }

    [Test]
    public async Task Empty_CreatesAdminThatMustChangePassword()
    {
        Assert.That(await Seeder("configured-password").SeedAsync(CancellationToken.None), Is.True);

        Assert.That(_inserted, Is.Not.Null);
        Assert.That(_inserted!.Username, Is.EqualTo("admin"));
        Assert.That(_inserted.Role, Is.EqualTo(UserRole.Admin));
        Assert.That(_inserted.MustChangePassword, Is.True);
        Assert.That(_inserted.IsDisabled, Is.False);
        Assert.That(_inserted.FailedLoginCount, Is.Zero);
        Assert.That(_inserted.SecurityStamp, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(_inserted.CreatedAt, Is.EqualTo(Now.UtcDateTime));
        Assert.That(_inserted.UpdatedAt, Is.EqualTo(Now.UtcDateTime));
        Assert.That(_inserted.CreatedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task ConfiguredPassword_IsHashedAndNotPrinted()
    {
        await Seeder("configured-password").SeedAsync(CancellationToken.None);

        Assert.That(_inserted!.PasswordHash, Does.Not.Contain("configured-password"));
        Assert.That(_hasher.VerifyHashedPassword(_inserted, _inserted.PasswordHash, "configured-password"),
            Is.EqualTo(PasswordVerificationResult.Success));
        Assert.That(_console.ToString(), Is.Empty);
    }

    [Test]
    public async Task NoConfiguredPassword_GeneratesOneAndPrintsItOnce()
    {
        await Seeder().SeedAsync(CancellationToken.None);

        var output = _console.ToString();
        var printed = output.Trim().Split(' ')[^1];
        Assert.That(printed, Does.Match("^[A-Za-z0-9_-]{20}$"));
        Assert.That(output.Split(printed).Length - 1, Is.EqualTo(1), "printed exactly once");
        Assert.That(_hasher.VerifyHashedPassword(_inserted!, _inserted!.PasswordHash, printed),
            Is.EqualTo(PasswordVerificationResult.Success));
    }
}
