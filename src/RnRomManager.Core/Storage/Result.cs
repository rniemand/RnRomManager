using Microsoft.Win32.SafeHandles;

namespace RnRomManager.Core.Storage;

internal readonly record struct Result(
    NativeCheckedOpenOutcome Outcome,
    SafeFileHandle? Handle,
    ExternalFileSnapshot? Snapshot,
    string? Details)
{
    public static Result Fail(NativeCheckedOpenOutcome outcome, string details) => new(outcome, null, null, details);
}
