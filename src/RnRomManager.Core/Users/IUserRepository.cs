namespace RnRomManager.Core.Users;

/// <summary>The <c>users</c> table. Username lookups are case-insensitive (column collation, spec §9.1).</summary>
public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <returns>The new user's id.</returns>
    Task<int> InsertAsync(User user, CancellationToken cancellationToken);
}
