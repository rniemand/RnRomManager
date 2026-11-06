namespace RnRomManager.Core.Users;

/// <summary>Role names stored in <c>user_roles</c>. A user has any subset; roles don't imply each other.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string RomManager = "rom-manager";

    public static readonly IReadOnlyList<string> All = [Admin, RomManager];

    public static readonly IReadOnlySet<string> None = Of();

    /// <summary>A sorted (ordinal) set, so serialized role lists come out in a stable order.</summary>
    public static IReadOnlySet<string> Of(params IEnumerable<string> roles) => new SortedSet<string>(roles, StringComparer.Ordinal);

    public const string ValidationError = "Roles must be any of: admin, rom-manager.";

    /// <returns>The set, or null when an entry is null, unknown or repeated. A null list is the empty set.</returns>
    public static IReadOnlySet<string>? Parse(IReadOnlyList<string?>? roles)
    {
        if (roles is null)
            return None;
        if (roles.Any(r => r is null || !All.Contains(r)) || roles.Distinct(StringComparer.Ordinal).Count() != roles.Count)
            return null;
        return Of(roles!);
    }
}
