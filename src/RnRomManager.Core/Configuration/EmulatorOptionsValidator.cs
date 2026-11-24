using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

/// <summary>
/// B1: each operator-supplied core path (<c>Emulator:GenesisCorePath</c>, <c>Emulator:PicodriveCorePath</c>,
/// <c>Emulator:Snes9xCorePath</c>, <c>Emulator:OperaCorePath</c>, <c>Emulator:ViceCorePath</c>, <c>Emulator:Mupen64PlusCorePath</c>, <c>Emulator:Mame2003PlusCorePath</c>, <c>Emulator:FbneoCorePath</c>) is empty or
/// absolute. Whether it exists is checked per request.
/// </summary>
public sealed class EmulatorOptionsValidator : IValidateOptions<EmulatorOptions>
{
    public ValidateOptionsResult Validate(string? name, EmulatorOptions options)
    {
        var failures = new List<string>();
        foreach (var (key, path) in new[]
        {
            ("Emulator:GenesisCorePath", options.GenesisCorePath),
            ("Emulator:PicodriveCorePath", options.PicodriveCorePath),
            ("Emulator:Snes9xCorePath", options.Snes9xCorePath),
            ("Emulator:OperaCorePath", options.OperaCorePath),
            ("Emulator:ViceCorePath", options.ViceCorePath),
            ("Emulator:Mupen64PlusCorePath", options.Mupen64PlusCorePath),
            ("Emulator:Mame2003PlusCorePath", options.Mame2003PlusCorePath),
            ("Emulator:FbneoCorePath", options.FbneoCorePath),
        })
        {
            if (path.Length != 0 && !Path.IsPathFullyQualified(path))
                failures.Add($"{key} must be empty or an absolute path (got '{path}').");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
