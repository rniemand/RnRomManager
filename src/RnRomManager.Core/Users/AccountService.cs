using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;

namespace RnRomManager.Core.Users;

/// <summary>Login with lockout, and password change (spec §9.1, §9.2).</summary>
public sealed class AccountService(
    IUserRepository users,
    IPasswordHasher<User> hasher,
    TimeProvider time,
    IAuditLog audit,
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

    private void Record(string action, User user, string? details = null) =>
        audit.Record(new AuditEvent(action, new AuditActor(user.Id, user.Username, null), Details: details));

    /// <summary>Records <c>SignedIn</c>, <c>SignInFailed</c> (with its reason) and <c>LockedOut</c> (U9).</summary>
    /// <returns>The user, or null for any failure (unknown, disabled, locked out, wrong password): the caller answers a generic 401.</returns>
    public async Task<User?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var user = await users.GetByUsernameAsync(username, cancellationToken);
        if (user is null)
        {
            hasher.VerifyHashedPassword(Dummy, _dummyHash.Value, password);
            audit.Record(new AuditEvent(AuditActions.SignInFailed, new AuditActor(null, username, null), Details: "reason=unknown-user"));
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (user.IsDisabled || user.LockoutUntil > now)
        {
            Record(AuditActions.SignInFailed, user, user.IsDisabled ? "reason=disabled" : "reason=locked-out");
            return null;
        }

        // shortcut: SuccessRehashNeeded is accepted without re-hashing; add it if the hasher's defaults ever change.
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            var failures = await users.IncrementFailedLoginCountAsync(user.Id, cancellationToken);
            if (failures >= MaxFailedLogins)
            {
                var until = now + LockoutDuration;
                await users.LockOutAsync(user.Id, until, now, cancellationToken);
                logger.LogWarning("User '{Username}' locked out until {LockoutUntil:o} after {Failures} failed logins", user.Username, until, failures);
                Record(AuditActions.SignInFailed, user, "reason=wrong-password");
                Record(AuditActions.LockedOut, user, $"until={until:yyyy-MM-ddTHH:mm:ssZ}");
                return null;
            }
            Record(AuditActions.SignInFailed, user, "reason=wrong-password");
            return null;
        }

        await users.RecordSuccessfulLoginAsync(user.Id, now, cancellationToken);
        Record(AuditActions.SignedIn, user);
        return user with { FailedLoginCount = 0, LockoutUntil = null, LastLoginAt = now };
    }

    /// <summary>Sets the new password, clears <c>must_change_password</c> and rotates the security stamp (other sessions die).</summary>
    /// <returns>The updated user, or validation errors keyed by request field.</returns>
    public async Task<(User? User, Dictionary<string, string[]>? Errors)> ChangePasswordAsync(
        User user, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        if (currentPassword is null || hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return (null, new() { ["currentPassword"] = ["Current password is incorrect."] });
        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
            return (null, new() { ["newPassword"] = ["New password must be different from the current password."] });
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
        Record(AuditActions.PasswordChanged, user);
        return (updated, null);
    }
}
