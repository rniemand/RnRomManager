using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Users;

namespace RnRomManager.Data.Repositories;

public sealed class UserRepository(IDbConnectionFactory connections) : IUserRepository
{
    private const string Select = """
        SELECT id AS Id, username AS Username, password_hash AS PasswordHash, role AS Role,
               is_disabled AS IsDisabled, must_change_password AS MustChangePassword,
               failed_login_count AS FailedLoginCount, lockout_until AS LockoutUntil,
               security_stamp AS SecurityStamp, created_at AS CreatedAt, updated_at AS UpdatedAt,
               last_login_at AS LastLoginAt
        FROM users
        """;

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM users", cancellationToken: cancellationToken));
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition($"{Select} WHERE username = @username", new { username }, cancellationToken: cancellationToken));
    }

    public async Task<int> InsertAsync(User user, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO users (username, password_hash, role, is_disabled, must_change_password, failed_login_count,
                               lockout_until, security_stamp, created_at, updated_at, last_login_at)
            VALUES (@Username, @PasswordHash, @Role, @IsDisabled, @MustChangePassword, @FailedLoginCount,
                    @LockoutUntil, @SecurityStamp, @CreatedAt, @UpdatedAt, @LastLoginAt);
            SELECT LAST_INSERT_ID();
            """;
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            user.Username,
            user.PasswordHash,
            Role = user.Role.ToString(),
            user.IsDisabled,
            user.MustChangePassword,
            user.FailedLoginCount,
            user.LockoutUntil,
            user.SecurityStamp,
            user.CreatedAt,
            user.UpdatedAt,
            user.LastLoginAt,
        }, cancellationToken: cancellationToken));
    }
}
