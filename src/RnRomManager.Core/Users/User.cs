namespace RnRomManager.Core.Users;

/// <summary>A row of <c>users</c> (spec §14.1). Times are UTC.</summary>
public sealed record User
{
    public int Id { get; init; }
    public required string Username { get; init; }
    public required string PasswordHash { get; init; }
    /// <summary>Stored in <c>user_roles</c>; see <see cref="Users.Roles"/>.</summary>
    public IReadOnlySet<string> Roles { get; init; } = Users.Roles.None;
    public bool IsDisabled { get; init; }
    public bool MustChangePassword { get; init; }
    public int FailedLoginCount { get; init; }
    public DateTime? LockoutUntil { get; init; }
    public required string SecurityStamp { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}
