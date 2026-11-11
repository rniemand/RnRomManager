using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RnRomManager.Core.Storage;

/// <summary>
/// Opens a regular file for reading so that no link is followed below the root, even when an entry is swapped while
/// the open is in progress (spec G4). The checks are made on the handles that were opened, never on a path again.
/// </summary>
internal static class NativeCheckedOpen
{
    internal enum Outcome
    {
        Opened,
        Missing,
        Unsafe,
        Failed,
    }

    internal readonly record struct Result(Outcome Outcome, SafeFileHandle? Handle, ExternalFileSnapshot? Snapshot, string? Details)
    {
        public static Result Fail(Outcome outcome, string details) => new(outcome, null, null, details);
    }

    /// <param name="fullRoot">Fully qualified, no trailing separator.</param>
    /// <param name="components">The names below the root, each as spelled on disk; the last is the file.</param>
    /// <param name="beforeDirectoryOpen">Test seam: runs with each directory's path right before it is opened.</param>
    /// <exception cref="PlatformNotSupportedException">Neither Windows nor Linux.</exception>
    public static Result OpenRead(string fullRoot, IReadOnlyList<string> components, FileShare share, Action<string>? beforeDirectoryOpen)
    {
        if (OperatingSystem.IsWindows())
            return OpenWindows(fullRoot, components, share, beforeDirectoryOpen);
        if (OperatingSystem.IsLinux())
            return OpenLinux(fullRoot, components, beforeDirectoryOpen);
        throw new PlatformNotSupportedException("External ROM storage needs Windows or Linux checked file opens.");
    }

    // --- Linux: directory-relative no-follow opens, then the pinned leaf through procfs ---

    private const int ORdOnly = 0;
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;
    private const int OPath = 0x200000;
    private const int ENOENT = 2;
    private const int ENOTDIR = 20;
    private const int ELOOP = 40;

    // The two flags that differ between the generic Linux ABI (x64) and the ARM one.
    private static readonly bool Arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm or Architecture.Armv6;
    private static readonly int ODirectory = Arm ? 0x4000 : 0x10000;
    private static readonly int ONoFollow = Arm ? 0x8000 : 0x20000;

    private static Result OpenLinux(string fullRoot, IReadOnlyList<string> components, Action<string>? beforeDirectoryOpen)
    {
        var held = new List<SafeFileHandle>();
        try
        {
            // The root is taken as configured: it may itself be a link (a mounted volume).
            var rootFd = open(fullRoot, OPath | ODirectory | OCloExec);
            if (rootFd < 0)
                return LinuxFailure(fullRoot, Marshal.GetLastPInvokeError(), directory: false);
            var current = Hold(held, rootFd);
            var path = fullRoot;

            for (var i = 0; i < components.Count - 1; i++)
            {
                path = Path.Combine(path, components[i]);
                beforeDirectoryOpen?.Invoke(path);
                // O_DIRECTORY | O_NOFOLLOW: a link here fails with ENOTDIR instead of being followed.
                var fd = openat(Fd(current), components[i], OPath | ODirectory | ONoFollow | OCloExec);
                if (fd < 0)
                    return LinuxFailure(path, Marshal.GetLastPInvokeError(), directory: true);
                current = Hold(held, fd);
            }

            path = Path.Combine(path, components[^1]);
            // Without O_DIRECTORY, O_PATH | O_NOFOLLOW pins whatever the name is, a link included, without opening it.
            var pinnedFd = openat(Fd(current), components[^1], OPath | ONoFollow | OCloExec);
            if (pinnedFd < 0)
                return LinuxFailure(path, Marshal.GetLastPInvokeError(), directory: false);
            var pinned = Hold(held, pinnedFd);
            var pinnedStat = NativeFileStat.Read(pinned);
            if (pinnedStat.Outcome != NativeStatOutcome.Found)
                return Result.Fail(Outcome.Failed, $"{path}: {pinnedStat.Error ?? "can't be inspected"}");
            if (pinnedStat.IsLink)
                return Result.Fail(Outcome.Unsafe, $"{path} is a link");
            if (!pinnedStat.IsRegularFile)
                return Result.Fail(Outcome.Unsafe, $"{path} is not a regular file");

            // Reopening through procfs opens the pinned object itself, whatever the name points at by now. There is
            // no fallback to the path: without procfs the open fails.
            var readFd = open("/proc/self/fd/" + pinnedFd.ToString(CultureInfo.InvariantCulture), ORdOnly | ONonBlock | OCloExec);
            if (readFd < 0)
                return Result.Fail(Outcome.Failed, $"{path} can't be opened through /proc/self/fd (errno {Marshal.GetLastPInvokeError()}); procfs must be mounted");
            var readable = new SafeFileHandle(readFd, ownsHandle: true);
            var readStat = NativeFileStat.Read(readable);
            if (readStat.Outcome != NativeStatOutcome.Found || !readStat.IsRegularFile || readStat.Snapshot!.Identity != pinnedStat.Snapshot!.Identity)
            {
                readable.Dispose();
                return Result.Fail(Outcome.Unsafe, $"{path} is not the file that was checked");
            }
            return new Result(Outcome.Opened, readable, readStat.Snapshot, null);
        }
        finally
        {
            foreach (var handle in held)
                handle.Dispose();
        }
    }

    private static SafeFileHandle Hold(List<SafeFileHandle> held, int fd)
    {
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        held.Add(handle);
        return handle;
    }

    private static int Fd(SafeFileHandle handle) => (int)handle.DangerousGetHandle(); // kept alive by the caller's list

    private static Result LinuxFailure(string path, int errno, bool directory) => errno switch
    {
        ENOENT => Result.Fail(Outcome.Missing, $"{path} does not exist"),
        ENOTDIR when directory => Result.Fail(Outcome.Unsafe, $"{path} is a link or not a directory"),
        ENOTDIR => Result.Fail(Outcome.Missing, $"{path} does not exist"),
        ELOOP => Result.Fail(Outcome.Unsafe, $"{path} is a link"),
        _ => Result.Fail(Outcome.Failed, $"{path} can't be opened (errno {errno})"),
    };

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    // --- Windows: hold each directory without delete sharing, then open the leaf itself ---

    private const uint FileListDirectory = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint ShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;

    private static Result OpenWindows(string fullRoot, IReadOnlyList<string> components, FileShare share, Action<string>? beforeDirectoryOpen)
    {
        var held = new List<SafeFileHandle>();
        try
        {
            var path = fullRoot;
            for (var i = 0; i < components.Count - 1; i++)
            {
                path = Path.Combine(path, components[i]);
                beforeDirectoryOpen?.Invoke(path);
                // No delete sharing: while the handle is held the directory can't be renamed away or replaced. Windows
                // applies sharing modes only to handles with data access, hence the list right.
                var directory = CreateFileW(NativePath.ToExtendedLength(path), FileListDirectory | FileReadAttributes, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting,
                    FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
                held.Add(directory);
                if (directory.IsInvalid)
                    return WindowsFailure(path, Marshal.GetLastPInvokeError());
                var stat = NativeFileStat.Read(directory);
                if (stat.Outcome != NativeStatOutcome.Found)
                    return Result.Fail(Outcome.Failed, $"{path}: {stat.Error ?? "can't be inspected"}");
                if (stat.IsLink || !stat.IsDirectory)
                    return Result.Fail(Outcome.Unsafe, $"{path} is a reparse point or not a directory");
            }

            path = Path.Combine(path, components[^1]);
            var shareMode = (share.HasFlag(FileShare.Read) ? ShareRead : 0) | (share.HasFlag(FileShare.Write) ? ShareWrite : 0)
                | (share.HasFlag(FileShare.Delete) ? ShareDelete : 0);
            // FILE_FLAG_OPEN_REPARSE_POINT opens a link itself, so the attribute check below sees and refuses it.
            var leaf = CreateFileW(NativePath.ToExtendedLength(path), GenericRead, shareMode, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
            if (leaf.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                leaf.Dispose();
                return WindowsFailure(path, error);
            }
            var leafStat = NativeFileStat.Read(leaf);
            if (leafStat.Outcome != NativeStatOutcome.Found || !leafStat.IsRegularFile)
            {
                leaf.Dispose();
                return leafStat.Outcome != NativeStatOutcome.Found
                    ? Result.Fail(Outcome.Failed, $"{path}: {leafStat.Error ?? "can't be inspected"}")
                    : Result.Fail(Outcome.Unsafe, leafStat.IsLink ? $"{path} is a reparse point" : $"{path} is not a regular file");
            }
            return new Result(Outcome.Opened, leaf, leafStat.Snapshot, null);
        }
        finally
        {
            foreach (var handle in held)
                handle.Dispose();
        }
    }

    private static Result WindowsFailure(string path, int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => Result.Fail(Outcome.Missing, $"{path} does not exist"),
        ErrorSharingViolation => Result.Fail(Outcome.Failed, $"{path} is in use by another process"),
        _ => Result.Fail(Outcome.Failed, $"{path} can't be opened (error {error})"),
    };

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
