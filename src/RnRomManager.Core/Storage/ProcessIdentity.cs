using System.Runtime.InteropServices;

namespace RnRomManager.Core.Storage;

/// <summary>Who the process runs as, for permission error messages (spec §17.2).</summary>
public static class ProcessIdentity
{
    public static string Describe() =>
        OperatingSystem.IsWindows()
            ? $"user {Environment.UserName}"
            : $"UID {getuid()} / GID {getgid()}";

    /// <summary>Root ignores file permissions (used by tests to skip permission checks).</summary>
    public static bool IsRoot() => !OperatingSystem.IsWindows() && geteuid() == 0;

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();

    [DllImport("libc")]
    private static extern uint geteuid();
}
