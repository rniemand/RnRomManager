using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Core.Users;

/// <summary>Admin user management and CLI password recovery (spec §9.1, D37). Users are disabled, never deleted.</summary>
public sealed class UserAdminService(
    IUserRepository users,
    IPasswordHasher<User> hasher,
    TimeProvider time,
    ILogger<UserAdminService> logger)
{
    /// <summary>New users must change the password on first login.</summary>
    public async Task<UserAdminResult> CreateAsync(string? username, IReadOnlyList<string?>? roles, string? password, CancellationToken cancellationToken)
    {
        if (UsernamePolicy.Validate(username) is { } usernameError)
            return UserAdminResult.Invalid("username", usernameError);
        if (Roles.Parse(roles) is not { } parsedRoles)
            return UserAdminResult.Invalid("roles", Roles.ValidationError);
        if (PasswordPolicy.Validate(password, username!) is { } passwordError)
            return UserAdminResult.Invalid("password", passwordError);

        var now = time.GetUtcNow().UtcDateTime;
        var user = new User
        {
            Username = username!,
            PasswordHash = "",
            Roles = parsedRoles,
            MustChangePassword = true,
            SecurityStamp = Secrets.NewSecurityStamp(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        user = user with { PasswordHash = hasher.HashPassword(user, password!) };
        try
        {
            user = user with { Id = await users.InsertAsync(user, cancellationToken) };
        }
        catch (UsernameTakenException)
        {
            return UserAdminResult.Fail(UserAdminResult.Failure.UsernameTaken);
        }
        logger.LogInformation("Created user '{Username}' with roles {Roles}", user.Username, string.Join(", ", user.Roles));
        return UserAdminResult.Ok(user);
    }

    /// <summary>Roles or disabled change (null = unchanged); either one rotates the security stamp so the user's sessions die (spec §9.2).</summary>
    public async Task<UserAdminResult> UpdateAsync(int id, IReadOnlyList<string?>? roles, bool? isDisabled, CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? parsedRoles = null;
        if (roles is not null && (parsedRoles = Roles.Parse(roles)) is null)
            return UserAdminResult.Invalid("roles", Roles.ValidationError);
        if (await users.GetByIdAsync(id, cancellationToken) is not { } user)
            return UserAdminResult.Fail(UserAdminResult.Failure.NotFound);

        var newRoles = parsedRoles ?? user.Roles;
        var newDisabled = isDisabled ?? user.IsDisabled;
        if (newRoles.SetEquals(user.Roles) && newDisabled == user.IsDisabled)
            return UserAdminResult.Ok(user);

        var updated = user with { Roles = newRoles, IsDisabled = newDisabled };

        updated = updated with { SecurityStamp = Secrets.NewSecurityStamp(), UpdatedAt = time.GetUtcNow().UtcDateTime };
        if (!await users.UpdateUnlessLastAdminAsync(updated, cancellationToken))
            return UserAdminResult.Fail(UserAdminResult.Failure.LastAdmin);

        logger.LogInformation("User '{Username}' updated: roles {Roles}, disabled {IsDisabled}", updated.Username, string.Join(", ", updated.Roles), updated.IsDisabled);
        return UserAdminResult.Ok(updated);
    }

    /// <summary>Admin reset: new password, lockout cleared, must change at next login, sessions die.</summary>
    public async Task<UserAdminResult> ResetPasswordAsync(int id, string? newPassword, CancellationToken cancellationToken)
    {
        if (await users.GetByIdAsync(id, cancellationToken) is not { } user)
            return UserAdminResult.Fail(UserAdminResult.Failure.NotFound);
        if (PasswordPolicy.Validate(newPassword, user.Username) is { } error)
            return UserAdminResult.Invalid("newPassword", error);

        var updated = WithNewPassword(user, newPassword!);
        await users.UpdateAsync(updated, cancellationToken);
        logger.LogInformation("Password of user '{Username}' was reset by an admin", user.Username);
        return UserAdminResult.Ok(updated);
    }

    /// <summary>
    /// CLI <c>--reset-password</c> (D37): random password, re-enabled, must change, and promoted to Admin when no
    /// other enabled admin exists (it then gets every role, as the seeded admin). The password is returned for the console only.
    /// </summary>
    /// <returns>null when the user doesn't exist.</returns>
    public async Task<(User User, string Password)?> RecoverAsync(string username, CancellationToken cancellationToken)
    {
        if (await users.GetByUsernameAsync(username, cancellationToken) is not { } user)
            return null;

        var otherAdmins = (await users.ListAsync(cancellationToken)).Any(u => !u.IsDisabled && u.Roles.Contains(Roles.Admin) && u.Id != user.Id);
        var password = Secrets.NewPassword();
        var updated = WithNewPassword(user with { IsDisabled = false, Roles = otherAdmins ? user.Roles : Roles.Of(Roles.All) }, password);
        await users.UpdateAsync(updated, cancellationToken);

        logger.LogWarning("Password of user '{Username}' was reset from the command line", user.Username);
        if (!updated.Roles.SetEquals(user.Roles))
            logger.LogWarning("User '{Username}' was promoted to Admin because no other enabled Admin exists", user.Username);
        return (updated, password);
    }

    private User WithNewPassword(User user, string password) => user with
    {
        PasswordHash = hasher.HashPassword(user, password),
        MustChangePassword = true,
        FailedLoginCount = 0,
        LockoutUntil = null,
        SecurityStamp = Secrets.NewSecurityStamp(),
        UpdatedAt = time.GetUtcNow().UtcDateTime,
    };

}
