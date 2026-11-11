using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RnRomManager.Core.Storage;

/// <summary>Which file a path named when it was looked at: device + inode on Linux, volume serial + file index on Windows.</summary>
public readonly record struct FileIdentity(ulong Volume, ulong FileId);

/// <summary>
/// A regular file as it was when checked: which file it is, its length and its last write time. Comparing two
/// snapshots tells whether a path still names the same, unchanged file.
/// </summary>
public sealed record ExternalFileSnapshot(FileIdentity Identity, long Length, DateTime LastWriteTimeUtc);

internal enum NativeStatOutcome
{
    Found,

    /// <summary>The entry, or a directory on the way to it, doesn't exist.</summary>
    NotFound,

    /// <summary>Anything else (permission, IO): the entry may exist but can't be inspected.</summary>
    Failed,
}

/// <summary>What one path or open handle names, read without following a link and without reading file content.</summary>
internal readonly record struct NativeFileStat(
    NativeStatOutcome Outcome, bool IsLink, bool IsDirectory, bool IsRegularFile, ExternalFileSnapshot? Snapshot, string? Error)
{
    /// <param name="fullPath">Fully qualified, as <see cref="Path.GetFullPath(string)"/> returns it.</param>
    /// <exception cref="PlatformNotSupportedException">Neither Windows nor Linux.</exception>
    public static NativeFileStat Read(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            return ReadWindows(fullPath);
        if (OperatingSystem.IsLinux())
            return ReadLinux(AtFdCwd, fullPath, AtSymlinkNoFollow);
        throw new PlatformNotSupportedException("External ROM storage needs Windows or Linux file identity calls.");
    }

    /// <summary>The object an open handle refers to, whatever its path names now.</summary>
    /// <exception cref="PlatformNotSupportedException">Neither Windows nor Linux.</exception>
    public static NativeFileStat Read(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
            return ReadWindows(handle);
        if (OperatingSystem.IsLinux())
        {
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                return ReadLinux((int)handle.DangerousGetHandle(), "", AtEmptyPath | AtSymlinkNoFollow);
            }
            finally
            {
                if (added)
                    handle.DangerousRelease();
            }
        }
        throw new PlatformNotSupportedException("External ROM storage needs Windows or Linux file identity calls.");
    }

    private static NativeFileStat Missing() => new(NativeStatOutcome.NotFound, false, false, false, null, null);

    private static NativeFileStat Failure(string error) => new(NativeStatOutcome.Failed, false, false, false, null, error);

    // --- Linux: statx has one layout on every architecture, unlike struct stat ---

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7FF;
    private const int StatxSize = 256;
    private const int ENOENT = 2;
    private const int ENOTDIR = 20;
    private const ushort ModeTypeMask = 0xF000;
    private const ushort ModeRegular = 0x8000;
    private const ushort ModeDirectory = 0x4000;
    private const ushort ModeLink = 0xA000;

    private static NativeFileStat ReadLinux(int directory, string path, int flags)
    {
        var buffer = new byte[StatxSize];
        if (statx(directory, path, flags, StatxBasicStats, buffer) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno is ENOENT or ENOTDIR ? Missing() : Failure($"statx failed with errno {errno}");
        }

        var span = buffer.AsSpan();
        var type = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(span[28..]) & ModeTypeMask);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[40..]);
        var mtimeSeconds = BinaryPrimitives.ReadInt64LittleEndian(span[112..]);
        var mtimeNanoseconds = BinaryPrimitives.ReadUInt32LittleEndian(span[120..]);
        ulong device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(span[136..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(span[140..]);
        var mtime = DateTime.UnixEpoch.AddTicks(mtimeSeconds * TimeSpan.TicksPerSecond + mtimeNanoseconds / 100);
        var regular = type == ModeRegular;
        return new NativeFileStat(NativeStatOutcome.Found, type == ModeLink, type == ModeDirectory, regular,
            regular ? new ExternalFileSnapshot(new FileIdentity(device, inode), size, mtime) : null, null);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags, uint mask, byte[] buffer);

    // --- Windows: an attribute-only handle on the entry itself (a reparse point is not followed) ---

    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const uint FileTypeDisk = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private static NativeFileStat ReadWindows(string fullPath)
    {
        using var handle = CreateFileW(NativePath.ToExtendedLength(fullPath), FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error is ErrorFileNotFound or ErrorPathNotFound ? Missing() : Failure($"CreateFile failed with error {error}");
        }
        return ReadWindows(handle);
    }

    private static NativeFileStat ReadWindows(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            return Failure($"GetFileInformationByHandle failed with error {Marshal.GetLastPInvokeError()}");

        var link = (info.FileAttributes & AttributeReparsePoint) != 0;
        var directory = !link && (info.FileAttributes & AttributeDirectory) != 0;
        var regular = !link && !directory && GetFileType(handle) == FileTypeDisk;
        var identity = new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        var length = (long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow);
        var mtime = DateTime.FromFileTimeUtc((long)(((ulong)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow));
        return new NativeFileStat(NativeStatOutcome.Found, link, directory, regular,
            regular ? new ExternalFileSnapshot(identity, length, mtime) : null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32", ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle file);
}
