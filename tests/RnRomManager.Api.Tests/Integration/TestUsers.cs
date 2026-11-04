using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Inserts users straight into the test database.</summary>
public static class TestUsers
{
    public const string Password = "correct-horse-battery";

    public static async Task<User> CreateAsync(
        ApiFactory factory, string username, UserRole role = UserRole.User, bool mustChangePassword = false)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = username,
            PasswordHash = "",
            Role = role,
            MustChangePassword = mustChangePassword,
            SecurityStamp = Secrets.NewSecurityStamp(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        user = user with { PasswordHash = new PasswordHasher<User>().HashPassword(user, Password) };
        return user with { Id = await factory.Services.GetRequiredService<IUserRepository>().InsertAsync(user, CancellationToken.None) };
    }
}
