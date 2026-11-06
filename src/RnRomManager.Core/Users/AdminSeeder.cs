using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Users;

/// <summary>
/// Startup step 8 (spec §4.1, §9.1): when <c>users</c> is empty, create <c>admin</c> with
/// <c>Admin:InitialPassword</c> or a random password that goes to the console only, never to the log.
/// </summary>
public sealed class AdminSeeder(
    IUserRepository users,
    IPasswordHasher<User> hasher,
    IOptions<AdminOptions> options,
    TimeProvider time,
    ILogger<AdminSeeder> logger)
{
    public const string AdminUsername = "admin";

    /// <summary>Where a generated password is printed. Deliberately not a logger (spec §9.1).</summary>
    public TextWriter Console { get; init; } = System.Console.Out;

    /// <returns>true when the admin user was created.</returns>
    public async Task<bool> SeedAsync(CancellationToken cancellationToken)
    {
        if (await users.CountAsync(cancellationToken) > 0)
            return false;

        var configured = options.Value.InitialPassword;
        var password = configured.Length > 0 ? configured : Secrets.NewPassword();
        var now = time.GetUtcNow().UtcDateTime;
        var admin = new User
        {
            Username = AdminUsername,
            PasswordHash = "",
            Roles = Roles.Of(Roles.Admin, Roles.RomManager),
            MustChangePassword = true,
            SecurityStamp = Secrets.NewSecurityStamp(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        admin = admin with { PasswordHash = hasher.HashPassword(admin, password) };

        await users.InsertAsync(admin, cancellationToken);

        if (configured.Length > 0)
        {
            logger.LogInformation("Created user '{Username}' with the password from Admin:InitialPassword; it must be changed at first login", AdminUsername);
        }
        else
        {
            logger.LogInformation("Created user '{Username}' with a generated password, printed once to the console; it must be changed at first login", AdminUsername);
            await Console.WriteLineAsync($"Initial password for user '{AdminUsername}' (shown once, must be changed at first login): {password}");
            await Console.FlushAsync(cancellationToken);
        }

        return true;
    }
}
