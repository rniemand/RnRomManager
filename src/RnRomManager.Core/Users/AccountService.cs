using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Core.Users;

/// <summary>Login with lockout, and password change (spec §9.1, §9.2).</summary>
public sealed class AccountService(
    IUserRepository users,
    IPasswordHasher<User> hasher,
    TimeProvider time,
    ILogger<AccountService> logger)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Unknown usernames still pay for one hash check, so response time doesn't reveal which names exist.
    private static readonly User Dummy = new()
    {
        Username = "", PasswordHash = "", SecurityStamp = "", CreatedAt = default, UpdatedAt = default,
    };
    private readonly Lazy<string> _dummyHash = new(() => hasher.HashPassword(Dummy, Secrets.NewPassword()));

    /// <returns>The user, or null for any failure (unknown, disabled, locked out, wrong password): the caller answers a generic 401.</returns>
    public async Task<User?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var user = await users.GetByUsernameAsync(username, cancellationToken);
        if (user is null)
        {
            hasher.VerifyHashedPassword(Dummy, _dummyHash.Value, password);
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (user.IsDisabled || user.LockoutUntil > now)
            return null;

        // shortcut: SuccessRehashNeeded is accepted without re-hashing; add it if the hasher's defaults ever change.
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            var failures = await users.IncrementFailedLoginCountAsync(user.Id, cancellationToken);
            if (failures >= MaxFailedLogins)
            {
                var until = now + LockoutDuration;
                await users.LockOutAsync(user.Id, until, now, cancellationToken);
                logger.LogWarning("User '{Username}' locked out until {LockoutUntil:o} after {Failures} failed logins", user.Username, until, failures);
            }
            return null;
        }

        await users.RecordSuccessfulLoginAsync(user.Id, now, cancellationToken);
        return user with { FailedLoginCount = 0, LockoutUntil = null, LastLoginAt = now };
    }

    /// <summary>Sets the new password, clears <c>must_change_password</c> and rotates the security stamp (other sessions die).</summary>
    /// <returns>The updated user, or validation errors keyed by request field.</returns>
    public async Task<(User? User, Dictionary<string, string[]>? Errors)> ChangePasswordAsync(
        User user, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        if (currentPassword is null || hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return (null, new() { ["currentPassword"] = ["Current password is incorrect."] });
        if (PasswordPolicy.Validate(newPassword, user.Username) is { } error)
            return (null, new() { ["newPassword"] = [error] });

        var updated = user with
        {
            PasswordHash = hasher.HashPassword(user, newPassword!),
            MustChangePassword = false,
            SecurityStamp = Secrets.NewSecurityStamp(),
            UpdatedAt = time.GetUtcNow().UtcDateTime,
        };
        await users.UpdateAsync(updated, cancellationToken);
        logger.LogInformation("User '{Username}' changed their password", user.Username);
        return (updated, null);
    }
}
