using Dapper;
using MySqlConnector;
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

    public async Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition($"{Select} WHERE id = @id", new { id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<User>(new CommandDefinition($"{Select} ORDER BY username", cancellationToken: cancellationToken))).AsList();
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
        try
        {
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
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new UsernameTakenException(user.Username, ex);
        }
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(UpdateSql, UpdateParameters(user), cancellationToken: cancellationToken));
    }

    public async Task<bool> UpdateUnlessLastAdminAsync(User user, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var enabledAdmins = await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT id FROM users WHERE role = 'Admin' AND is_disabled = 0 FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken));
        var staysAdmin = user is { Role: UserRole.Admin, IsDisabled: false };
        if (!staysAdmin && !enabledAdmins.Any(id => id != user.Id))
            return false;

        await connection.ExecuteAsync(new CommandDefinition(UpdateSql, UpdateParameters(user), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static object UpdateParameters(User user) => new
    {
        user.Id,
        user.PasswordHash,
        Role = user.Role.ToString(),
        user.IsDisabled,
        user.MustChangePassword,
        user.FailedLoginCount,
        user.LockoutUntil,
        user.SecurityStamp,
        user.UpdatedAt,
        user.LastLoginAt,
    };

    private const string UpdateSql = """
        UPDATE users SET password_hash = @PasswordHash, role = @Role, is_disabled = @IsDisabled,
                         must_change_password = @MustChangePassword, failed_login_count = @FailedLoginCount,
                         lockout_until = @LockoutUntil, security_stamp = @SecurityStamp, updated_at = @UpdatedAt,
                         last_login_at = @LastLoginAt
        WHERE id = @Id
        """;

    public async Task<int> IncrementFailedLoginCountAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE users SET failed_login_count = LAST_INSERT_ID(failed_login_count + 1) WHERE id = @id;
            SELECT LAST_INSERT_ID();
            """;
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
    }

    public async Task LockOutAsync(int id, DateTime until, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET lockout_until = @until, failed_login_count = 0, updated_at = @now WHERE id = @id",
            new { id, until, now }, cancellationToken: cancellationToken));
    }

    public async Task RecordSuccessfulLoginAsync(int id, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET failed_login_count = 0, lockout_until = NULL, last_login_at = @now WHERE id = @id",
            new { id, now }, cancellationToken: cancellationToken));
    }
}
