using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Startup step 8 against a real database (spec §4.1, §9.1).</summary>
[Category("Integration")]
public partial class AdminSeedTests
{
    private TextWriter _originalOut = null!;
    private StringWriter _console = null!;

    [SetUp]
    public void SetUp()
    {
        _originalOut = Console.Out;
        _console = new StringWriter();
        Console.SetOut(_console);
    }

    [TearDown]
    public void TearDown()
    {
        Console.SetOut(_originalOut);
        _console.Dispose();
    }

    [Test]
    public async Task FirstStart_PrintsGeneratedPasswordToConsole_ButNeverToTheLogFile()
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());

        using (factory.CreateClient())
            LogManager.Flush();

        var match = PrintedPassword().Match(_console.ToString());
        Assert.That(match.Success, Is.True, "the generated password is printed");
        var password = match.Groups[1].Value;

        var log = ReadLogs(factory);
        Assert.That(log, Does.Contain("Created user 'admin' with a generated password"));
        Assert.That(log, Does.Not.Contain(password));

        var admin = await factory.Services.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin", CancellationToken.None);
        Assert.That(admin!.MustChangePassword, Is.True);
        Assert.That(new PasswordHasher<User>().VerifyHashedPassword(admin, admin.PasswordHash, password),
            Is.EqualTo(PasswordVerificationResult.Success));
    }

    [Test]
    public async Task SecondStart_DoesNotSeedAgain()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();
        User? first;
        using (var factory = new ApiFactory(cs))
        {
            using var _ = factory.CreateClient();
            first = await factory.Services.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin", CancellationToken.None);
        }
        _console.GetStringBuilder().Clear();

        using (var factory = new ApiFactory(cs))
        {
            using var _ = factory.CreateClient();
            var users = factory.Services.GetRequiredService<IUserRepository>();

            Assert.That(await users.CountAsync(CancellationToken.None), Is.EqualTo(1));
            Assert.That(await users.GetByUsernameAsync("admin", CancellationToken.None), Is.EqualTo(first));
        }
        Assert.That(PrintedPassword().IsMatch(_console.ToString()), Is.False, "nothing printed on the second start");
    }

    [Test]
    public async Task ConfiguredInitialPassword_IsUsedAndNeverPrintedOrLogged()
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        factory.Settings["Admin:InitialPassword"] = "configured-initial-pw";

        using (factory.CreateClient())
            LogManager.Flush();

        var admin = await factory.Services.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin", CancellationToken.None);
        Assert.That(new PasswordHasher<User>().VerifyHashedPassword(admin!, admin!.PasswordHash, "configured-initial-pw"),
            Is.EqualTo(PasswordVerificationResult.Success));
        Assert.That(_console.ToString(), Does.Not.Contain("configured-initial-pw"));
        Assert.That(ReadLogs(factory), Does.Not.Contain("configured-initial-pw"));
    }

    private static string ReadLogs(ApiFactory factory)
    {
        var file = Directory.GetFiles(factory.Settings["Paths:Logs"]!, "rnrommanager-*.log").Single();
        // NLog keeps the file open (keepFileOpen).
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    [GeneratedRegex(@"Initial password for user 'admin' \(shown once, must be changed at first login\): (\S+)")]
    private static partial Regex PrintedPassword();
}
