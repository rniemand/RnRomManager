namespace RnRomManager.Core.Users;

/// <summary>The <c>users</c> table. Username lookups are case-insensitive (column collation, spec ยง9.1).</summary>
public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <returns>The new user's id.</returns>
    Task<int> InsertAsync(User user, CancellationToken cancellationToken);

    /// <summary>Writes every column except <c>id</c>, <c>username</c> and <c>created_at</c>.</summary>
    Task UpdateAsync(User user, CancellationToken cancellationToken);

    /// <summary>Atomic <c>failed_login_count + 1</c>, so parallel bad logins are all counted.</summary>
    /// <returns>The new count.</returns>
    Task<int> IncrementFailedLoginCountAsync(int id, CancellationToken cancellationToken);

    /// <summary>Sets <c>lockout_until</c> and resets the failure count (spec ง9.1).</summary>
    Task LockOutAsync(int id, DateTime until, DateTime now, CancellationToken cancellationToken);

    /// <summary>Resets the failure count and lockout, sets <c>last_login_at</c>.</summary>
    Task RecordSuccessfulLoginAsync(int id, DateTime now, CancellationToken cancellationToken);
}
