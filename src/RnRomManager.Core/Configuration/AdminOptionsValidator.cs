using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

/// <summary>Empty, or meets the spec §9.1 password policy for the <c>admin</c> user.</summary>
public sealed class AdminOptionsValidator : IValidateOptions<AdminOptions>
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        var password = options.InitialPassword;
        if (password.Length == 0)
            return ValidateOptionsResult.Success;

        // "Must not equal the username" can't fail: "admin" is shorter than MinLength.
        // The value is never echoed back (spec §13 redaction).
        return password.Length is < MinLength or > MaxLength
            ? ValidateOptionsResult.Fail($"Admin:InitialPassword must be empty or {MinLength}-{MaxLength} characters.")
            : ValidateOptionsResult.Success;
    }
}
