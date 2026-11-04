using System.Globalization;
using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

public sealed class IntegrityOptionsValidator : IValidateOptions<IntegrityOptions>
{
    public ValidateOptionsResult Validate(string? name, IntegrityOptions options) =>
        options.QuickCheckTime.Length == 0
        || TimeOnly.TryParseExact(options.QuickCheckTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Integrity:QuickCheckTime must be HH:mm or empty (got '{options.QuickCheckTime}').");
}
