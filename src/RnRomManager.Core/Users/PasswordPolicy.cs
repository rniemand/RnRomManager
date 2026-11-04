namespace RnRomManager.Core.Users;

/// <summary>Spec §9.1: 12–128 characters and not the username. No composition rules.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <returns>null when valid, else a message for the user. Never contains the password.</returns>
    public static string? Validate(string? password, string username)
    {
        if (password is null || password.Length is < MinLength or > MaxLength)
            return $"Password must be {MinLength}-{MaxLength} characters.";
        // Usernames compare case-insensitively everywhere, so this does too.
        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            return "Password must not be the same as the username.";
        return null;
    }
}
