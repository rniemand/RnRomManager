using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

/// <summary>Genesis B4: <c>Emulator:GenesisCorePath</c> is empty or absolute. Whether it exists is checked per request.</summary>
public sealed class EmulatorOptionsValidator : IValidateOptions<EmulatorOptions>
{
    public ValidateOptionsResult Validate(string? name, EmulatorOptions options)
    {
        var path = options.GenesisCorePath;
        return path.Length == 0 || Path.IsPathFullyQualified(path)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Emulator:GenesisCorePath must be empty or an absolute path (got '{path}').");
    }
}
