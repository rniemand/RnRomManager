namespace RnRomManager.Core.Library;

/// <summary>A written entry didn't match <c>info.json</c> (<c>VerifyFailed</c>, spec §6.1, §7.2 step 9).</summary>
public sealed class LibraryZipVerifyException(string entryName, string problem, Exception? inner = null)
    : Exception($"ZIP entry '{entryName}': {problem}", inner)
{
    public string EntryName { get; } = entryName;
}
