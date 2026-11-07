namespace RnRomManager.Core.Users;

/// <summary>The <c>users</c> table. Username lookups are case-insensitive (column collation, spec §9.1).</summary>
public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>All users, ordered by username.</summary>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken);

    /// <returns>The new user's id.</returns>
    /// <exception cref="UsernameTakenException">The name exists, ignoring case.</exception>
    Task<int> InsertAsync(User user, CancellationToken cancellationToken);

    /// <summary>Writes every column except <c>id</c>, <c>username</c> and <c>created_at</c>.</summary>
    Task UpdateAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="UpdateAsync"/>, but refuses (returns false) when the change would leave no enabled Admin.
    /// The check and the write share one transaction that locks the Admin rows (spec §9.1 <c>LastAdmin</c>).
    /// </summary>
    Task<bool> UpdateUnlessLastAdminAsync(User user, CancellationToken cancellationToken);

    /// <summary>Atomic <c>failed_login_count + 1</c>, so parallel bad logins are all counted.</summary>
    /// <returns>The new count.</returns>
    Task<int> IncrementFailedLoginCountAsync(int id, CancellationToken cancellationToken);

    /// <summary>Sets <c>lockout_until</c> and resets the failure count (spec §9.1).</summary>
    Task LockOutAsync(int id, DateTime until, DateTime now, CancellationToken cancellationToken);

    /// <summary>Resets the failure count and lockout, sets <c>last_login_at</c>.</summary>
    Task RecordSuccessfulLoginAsync(int id, DateTime now, CancellationToken cancellationToken);

    /// <summary>B23: the stored hotkey overrides (JSON), or null for all defaults.</summary>
    Task<string?> GetHotkeysAsync(int id, CancellationToken cancellationToken);

    /// <summary>B23: replaces the hotkey overrides; null resets to the defaults. Not a profile edit: no stamp change.</summary>
    Task SetHotkeysAsync(int id, string? hotkeys, CancellationToken cancellationToken);
}
