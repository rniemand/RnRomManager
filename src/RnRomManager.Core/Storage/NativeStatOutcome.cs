namespace RnRomManager.Core.Storage;

internal enum NativeStatOutcome
{
    Found,

    /// <summary>The entry, or a directory on the way to it, doesn't exist.</summary>
    NotFound,

    /// <summary>Anything else (permission, IO): the entry may exist but can't be inspected.</summary>
    Failed,
}
