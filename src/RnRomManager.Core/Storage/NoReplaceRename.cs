using System.Runtime.InteropServices;

namespace RnRomManager.Core.Storage;

/// <summary>How a <see cref="NoReplaceRename.Move"/> ended.</summary>
public enum NoReplaceRenameStatus
{
    Renamed,

    /// <summary>Something already has the destination name; it was left untouched.</summary>
    DestinationExists,

    /// <summary>Source and destination are on different volumes: only a copy can move the file. Nothing was changed.</summary>
    CrossDevice,

    /// <summary>The OS or this filesystem has no rename that refuses to replace (e.g. an NFS mount). Nothing was changed.</summary>
    Unsupported,

    /// <summary>The source, or the destination's folder, doesn't exist.</summary>
    NotFound,

    /// <summary>Any other failure (permissions, a sharing violation, IO). Nothing was renamed.</summary>
    Failed,
}

/// <param name="NativeError">The Win32 error or errno; 0 on success.</param>
/// <param name="Details">Why, for every status but <see cref="NoReplaceRenameStatus.Renamed"/>.</param>
public readonly record struct NoReplaceRenameResult(NoReplaceRenameStatus Status, int NativeError = 0, string? Details = null)
{
    public bool Renamed => Status == NoReplaceRenameStatus.Renamed;
}

/// <summary>
/// A rename within one filesystem that never replaces an existing entry and never falls back to a copy (spec W6):
/// <c>MoveFileExW</c> without <c>MOVEFILE_REPLACE_EXISTING</c> or <c>MOVEFILE_COPY_ALLOWED</c> on Windows,
/// <c>renameat2</c> with <c>RENAME_NOREPLACE</c> on Linux. <see cref="File.Move(string, string)"/> can't be used
/// for this: across volumes it copies silently, so the caller can't tell a rename from a copy. The check and the
/// rename are one atomic step, so two writers can't both win a name. On Linux the name is matched exactly; callers
/// that treat names as case-insensitive check for other spellings themselves.
/// </summary>
public static class NoReplaceRename
{
    /// <summary>Both paths fully qualified, as <see cref="Path.GetFullPath(string)"/> returns them.</summary>
    /// <exception cref="PlatformNotSupportedException">Neither Windows nor Linux.</exception>
    public static NoReplaceRenameResult Move(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            // Extended-length paths keep a valid name usable past MAX_PATH without the machine's long-path setting.
            return MoveFileExW(NativePath.ToExtendedLength(source), NativePath.ToExtendedLength(destination), 0)
                ? new NoReplaceRenameResult(NoReplaceRenameStatus.Renamed)
                : FromWindowsError(Marshal.GetLastPInvokeError(), source, destination);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                return renameat2(AtFdCwd, source, AtFdCwd, destination, RenameNoReplace) == 0
                    ? new NoReplaceRenameResult(NoReplaceRenameStatus.Renamed)
                    : FromErrno(Marshal.GetLastPInvokeError(), source, destination);
            }
            catch (EntryPointNotFoundException)
            {
                // A C library without the renameat2 wrapper.
                return FromErrno(ENOSYS, source, destination);
            }
        }

        throw new PlatformNotSupportedException("External ROM storage needs a Windows or Linux no-replace rename.");
    }

    /// <summary>The <see cref="NoReplaceRenameStatus.Unsupported"/> text callers put in their <c>IoError</c> details.</summary>
    public static string UnsupportedDetails(string destination) => $"no-replace rename is not supported for {destination}";

    /// <summary>
    /// Publishes a verified staging file under a free name. If the native no-replace rename is unavailable,
    /// a hard link claims the final name atomically. The staging alias is then removed on a best-effort basis.
    /// </summary>
    internal static NoReplaceRenameResult PublishStaged(string staging, string destination, ExternalFileSnapshot expected,
        Func<string, string, NoReplaceRenameResult>? rename = null) =>
        MoveVerified(staging, destination, expected, rename, requireSourceRemoval: false);

    /// <summary>
    /// Moves a verified file under a free name. The hard-link fallback reports success only when the original
    /// name is free. If removing it fails, the verified destination is kept for recovery.
    /// </summary>
    internal static NoReplaceRenameResult MoveVerified(string source, string destination, ExternalFileSnapshot expected,
        Func<string, string, NoReplaceRenameResult>? rename = null) =>
        MoveVerified(source, destination, expected, rename, requireSourceRemoval: true);

    private static NoReplaceRenameResult MoveVerified(string source, string destination, ExternalFileSnapshot expected,
        Func<string, string, NoReplaceRenameResult>? rename, bool requireSourceRemoval)
    {
        var result = (rename ?? Move)(source, destination);
        if (result.Status != NoReplaceRenameStatus.Unsupported)
            return result;
        if (NativeFileStat.Read(source).Snapshot != expected)
            return new(NoReplaceRenameStatus.Failed, Details: $"{source} is no longer the verified file");

        var linked = Link(source, destination);
        if (!linked.Renamed)
            return linked with { Details = $"{UnsupportedDetails(destination)}; {linked.Details}" };
        if (NativeFileStat.Read(destination).Snapshot != expected)
            return new(NoReplaceRenameStatus.Failed, Details: $"{destination} is no longer the verified published file");

        try
        {
            if (NativeFileStat.Read(source).Snapshot == expected)
                File.Delete(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (requireSourceRemoval)
                return new(NoReplaceRenameStatus.Failed, Details: $"{source} couldn't be removed after linking it: {ex.Message}");
            // The final name already holds the verified file. A leftover staging alias can be found by integrity.
        }
        if (requireSourceRemoval && NativeFileStat.Read(source).Outcome != NativeStatOutcome.NotFound)
            return new(NoReplaceRenameStatus.Failed, Details: $"{source} is still occupied after linking it");
        return linked;
    }

    private static NoReplaceRenameResult Link(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateHardLinkW(NativePath.ToExtendedLength(destination), NativePath.ToExtendedLength(source), IntPtr.Zero))
                return new(NoReplaceRenameStatus.Renamed);
            var error = Marshal.GetLastPInvokeError();
            return error is ErrorAlreadyExists or ErrorFileExists
                ? FromWindowsError(error, source, destination)
                : new(NoReplaceRenameStatus.Failed, error, $"creating a hard link at {destination} failed with error {error}");
        }

        if (link(source, destination) == 0)
            return new(NoReplaceRenameStatus.Renamed);
        var errno = Marshal.GetLastPInvokeError();
        var result = FromErrno(errno, source, destination);
        return result.Status == NoReplaceRenameStatus.DestinationExists
            ? result
            : result with { Details = $"creating a hard link at {destination} failed with errno {errno}" };
    }

    // --- Windows ---

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    public static NoReplaceRenameResult FromWindowsError(int error, string source, string destination) => error switch
    {
        ErrorAlreadyExists or ErrorFileExists => new(NoReplaceRenameStatus.DestinationExists, error, $"{destination} already exists"),
        ErrorNotSameDevice => new(NoReplaceRenameStatus.CrossDevice, error, $"{source} and {destination} are on different volumes"),
        ErrorFileNotFound or ErrorPathNotFound => new(NoReplaceRenameStatus.NotFound, error, $"{source} or the folder of {destination} does not exist"),
        _ => new(NoReplaceRenameStatus.Failed, error, $"renaming {source} to {destination} failed with error {error}"),
    };

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    // --- Linux ---

    private const int AtFdCwd = -100;
    private const uint RenameNoReplace = 1;
    private const int ENOENT = 2;
    private const int EEXIST = 17;
    private const int EXDEV = 18;
    private const int ENOTDIR = 20;
    private const int EINVAL = 22;
    private const int ENOSYS = 38;
    private const int EOPNOTSUPP = 95;

    public static NoReplaceRenameResult FromErrno(int errno, string source, string destination) => errno switch
    {
        EEXIST => new(NoReplaceRenameStatus.DestinationExists, errno, $"{destination} already exists"),
        EXDEV => new(NoReplaceRenameStatus.CrossDevice, errno, $"{source} and {destination} are on different filesystems"),
        // EINVAL: the filesystem rejects RENAME_NOREPLACE; ENOSYS: the kernel or C library has no renameat2.
        EINVAL or ENOSYS or EOPNOTSUPP => new(NoReplaceRenameStatus.Unsupported, errno, UnsupportedDetails(destination)),
        ENOENT or ENOTDIR => new(NoReplaceRenameStatus.NotFound, errno, $"{source} or the folder of {destination} does not exist"),
        _ => new(NoReplaceRenameStatus.Failed, errno, $"renaming {source} to {destination} failed with errno {errno}"),
    };

    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(int oldDirFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
