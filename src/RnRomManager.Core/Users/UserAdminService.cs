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
    public async Task<UserAdminResult> CreateAsync(string? username, string? role, string? password, CancellationToken cancellationToken)
    {
        if (UsernamePolicy.Validate(username) is { } usernameError)
            return UserAdminResult.Invalid("username", usernameError);
        if (ParseRole(role) is not { } parsedRole)
            return UserAdminResult.Invalid("role", RoleError);
        if (PasswordPolicy.Validate(password, username!) is { } passwordError)
            return UserAdminResult.Invalid("password", passwordError);

        var now = time.GetUtcNow().UtcDateTime;
        var user = new User
        {
            Username = username!,
            PasswordHash = "",
            Role = parsedRole,
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
        logger.LogInformation("Created user '{Username}' with role {Role}", user.Username, user.Role);
        return UserAdminResult.Ok(user);
    }

    /// <summary>Role or disabled change; either one rotates the security stamp so the user's sessions die (spec §9.2).</summary>
    public async Task<UserAdminResult> UpdateAsync(int id, string? role, bool? isDisabled, CancellationToken cancellationToken)
    {
        UserRole? parsedRole = null;
        if (role is not null && (parsedRole = ParseRole(role)) is null)
            return UserAdminResult.Invalid("role", RoleError);
        if (await users.GetByIdAsync(id, cancellationToken) is not { } user)
            return UserAdminResult.Fail(UserAdminResult.Failure.NotFound);

        var updated = user with { Role = parsedRole ?? user.Role, IsDisabled = isDisabled ?? user.IsDisabled };
        if (updated == user)
            return UserAdminResult.Ok(user);

        updated = updated with { SecurityStamp = Secrets.NewSecurityStamp(), UpdatedAt = time.GetUtcNow().UtcDateTime };
        if (!await users.UpdateUnlessLastAdminAsync(updated, cancellationToken))
            return UserAdminResult.Fail(UserAdminResult.Failure.LastAdmin);

        logger.LogInformation("User '{Username}' updated: role {Role}, disabled {IsDisabled}", updated.Username, updated.Role, updated.IsDisabled);
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
    /// other enabled Admin exists. The password is returned for the console only.
    /// </summary>
    /// <returns>null when the user doesn't exist.</returns>
    public async Task<(User User, string Password)?> RecoverAsync(string username, CancellationToken cancellationToken)
    {
        if (await users.GetByUsernameAsync(username, cancellationToken) is not { } user)
            return null;

        var otherAdmins = (await users.ListAsync(cancellationToken)).Any(u => u is { Role: UserRole.Admin, IsDisabled: false } && u.Id != user.Id);
        var password = Secrets.NewPassword();
        var updated = WithNewPassword(user with { IsDisabled = false, Role = otherAdmins ? user.Role : UserRole.Admin }, password);
        await users.UpdateAsync(updated, cancellationToken);

        logger.LogWarning("Password of user '{Username}' was reset from the command line", user.Username);
        if (updated.Role != user.Role)
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

    private const string RoleError = "Role must be 'Admin' or 'User'.";

    // Exact names only: Enum.TryParse would also accept "1" or "admin".
    private static UserRole? ParseRole(string? role) =>
        Enum.GetNames<UserRole>().Contains(role) ? Enum.Parse<UserRole>(role!) : null;
}
