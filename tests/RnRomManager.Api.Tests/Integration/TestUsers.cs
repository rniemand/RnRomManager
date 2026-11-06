using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Inserts users straight into the test database.</summary>
public static class TestUsers
{
    public const string Password = "correct-horse-battery";

    /// <summary>What a migrated <c>Admin</c> has (R3): every role.</summary>
    public static readonly IReadOnlySet<string> AllRoles = Roles.Of(Roles.All);

    public static async Task<User> CreateAsync(
        ApiFactory factory, string username, IReadOnlySet<string>? roles = null, bool mustChangePassword = false)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = username,
            PasswordHash = "",
            Roles = roles ?? Roles.None,
            MustChangePassword = mustChangePassword,
            SecurityStamp = Secrets.NewSecurityStamp(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        user = user with { PasswordHash = new PasswordHasher<User>().HashPassword(user, Password) };
        return user with { Id = await factory.Services.GetRequiredService<IUserRepository>().InsertAsync(user, CancellationToken.None) };
    }
}
