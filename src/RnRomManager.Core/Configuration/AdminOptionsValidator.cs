using Microsoft.Extensions.Options;
using RnRomManager.Core.Users;

namespace RnRomManager.Core.Configuration;

/// <summary>Empty, or meets the spec §9.1 password policy for the <c>admin</c> user.</summary>
public sealed class AdminOptionsValidator : IValidateOptions<AdminOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        var password = options.InitialPassword;
        if (password.Length == 0)
            return ValidateOptionsResult.Success;

        // The policy's message never contains the password (spec §13 redaction).
        var error = PasswordPolicy.Validate(password, AdminSeeder.AdminUsername);
        return error is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Admin:InitialPassword must be empty or a valid password. {error}");
    }
}
