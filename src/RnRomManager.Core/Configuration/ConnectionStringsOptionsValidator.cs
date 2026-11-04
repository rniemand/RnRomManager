using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

public sealed class ConnectionStringsOptionsValidator : IValidateOptions<ConnectionStringsOptions>
{
    public ValidateOptionsResult Validate(string? name, ConnectionStringsOptions options) =>
        string.IsNullOrWhiteSpace(options.Default)
            ? ValidateOptionsResult.Fail("ConnectionStrings:Default is required.")
            : ValidateOptionsResult.Success;
}
