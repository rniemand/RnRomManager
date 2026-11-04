using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Cli;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary><c>--reset-password</c> CLI (spec §9.1, D37) against MariaDB.</summary>
[Category("Integration")]
public partial class ResetPasswordCommandTests
{
    private ApiFactory _factory = null!;
    private StringWriter _stdout = null!;
    private StringWriter _stderr = null!;

    [SetUp]
    public async Task SetUp()
    {
        // The factory migrates + seeds the DB; the CLI then runs against it like a second process would.
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.CreateClient().Dispose();
        _stdout = new StringWriter();
        _stderr = new StringWriter();
    }

    [TearDown]
    public void TearDown()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        _factory.Dispose();
    }

    private IUserRepository Users => _factory.Services.GetRequiredService<IUserRepository>();

    private string[] Args() => _factory.Settings.Select(s => $"--{s.Key}={s.Value}").ToArray();

    private Task<int> RunAsync(string username) => ResetPasswordCommand.RunAsync(Args(), username, _stdout, _stderr);

    [Test]
    public async Task UnknownUser_Exits1WithAMessageOnStderr()
    {
        Assert.That(await RunAsync("nobody"), Is.EqualTo(1));
        Assert.That(_stderr.ToString(), Does.Contain("User 'nobody' not found."));
        Assert.That(_stdout.ToString(), Is.Empty);
    }

    [Test]
    public async Task InvalidConfig_Exits1BeforeTouchingTheDatabase()
    {
        _factory.Settings["Paths:Roms"] = "relative";

        Assert.That(await RunAsync("admin"), Is.EqualTo(1));
        Assert.That(_stderr.ToString(), Does.StartWith("Password reset failed:"));
    }

    [Test]
    public async Task Success_PrintsANewPassword_RotatesTheStamp_ReEnablesAndForcesAChange()
    {
        var plain = await TestUsers.CreateAsync(_factory, "plain");
        await Users.UpdateAsync(plain with { IsDisabled = true }, CancellationToken.None);
        await Users.LockOutAsync(plain.Id, DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow, CancellationToken.None);

        Assert.That(await RunAsync("PLAIN"), Is.EqualTo(0), _stderr.ToString());

        var match = PrintedPassword().Match(_stdout.ToString());
        Assert.That(match.Success, Is.True, _stdout.ToString());
        var after = (await Users.GetByIdAsync(plain.Id, CancellationToken.None))!;
        Assert.That(after.SecurityStamp, Is.Not.EqualTo(plain.SecurityStamp));
        Assert.That(after.IsDisabled, Is.False);
        Assert.That(after.MustChangePassword, Is.True);
        Assert.That(after.LockoutUntil, Is.Null);
        Assert.That(after.Role, Is.EqualTo(UserRole.User), "the seeded admin is still enabled, so no promotion");
        Assert.That(new PasswordHasher<User>().VerifyHashedPassword(after, after.PasswordHash, match.Groups[1].Value),
            Is.Not.EqualTo(PasswordVerificationResult.Failed));
    }

    [Test]
    public async Task NoOtherEnabledAdmin_PromotesTheUser()
    {
        var seeded = (await Users.GetByUsernameAsync("admin", CancellationToken.None))!;
        await Users.UpdateAsync(seeded with { IsDisabled = true }, CancellationToken.None);
        var plain = await TestUsers.CreateAsync(_factory, "plain");

        Assert.That(await RunAsync("plain"), Is.EqualTo(0), _stderr.ToString());

        Assert.That((await Users.GetByIdAsync(plain.Id, CancellationToken.None))!.Role, Is.EqualTo(UserRole.Admin));
        Assert.That(_stdout.ToString(), Does.Contain("role Admin"));
    }

    [Test]
    public async Task Dispatcher_MissingUsername_PrintsUsage()
    {
        var originalError = Console.Error;
        Console.SetError(_stderr);
        try
        {
            Assert.That(await CliDispatcher.TryRunAsync(["--reset-password"]), Is.EqualTo(1));
        }
        finally
        {
            Console.SetError(originalError);
        }
        Assert.That(_stderr.ToString(), Does.Contain("Usage: --reset-password <username>"));
    }

    [GeneratedRegex(@"New password for user 'plain' \(shown once, must be changed at next login, role \w+\): (\S+)")]
    private static partial Regex PrintedPassword();
}
