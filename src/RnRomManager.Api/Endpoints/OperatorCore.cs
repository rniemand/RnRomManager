using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// B1: one EmulatorJS core the operator supplies (never bundled): its id, the name messages use, the option holding its
/// folder, the problem code of a mismatch and the only files <c>/api/emulator/cores/{core}</c> serves.
/// </summary>
public sealed record OperatorCore(
    string Id,
    string DisplayName,
    Func<EmulatorOptions, string> PathOf,
    string MismatchCode,
    IReadOnlyList<OperatorCoreFile> Files)
{
    /// <summary>The manifest entry with exactly this name (ordinal), or null.</summary>
    public OperatorCoreFile? Find(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}
