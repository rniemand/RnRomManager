using System.Text.RegularExpressions;

namespace RnRomManager.Core.Users;

/// <summary>Spec §9.1: 3–64 chars of <c>[A-Za-z0-9._-]</c>. Uniqueness (case-insensitive) is the DB's job.</summary>
public static partial class UsernamePolicy
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    /// <returns>null when valid, else a message for the user.</returns>
    public static string? Validate(string? username) =>
        username is not null && Allowed().IsMatch(username)
            ? null
            : $"Username must be {MinLength}-{MaxLength} characters: letters, digits, '.', '_' or '-'.";

    // \z, not $: in .NET `$` also matches before a trailing "\n".
    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,64}\z")]
    private static partial Regex Allowed();
}
