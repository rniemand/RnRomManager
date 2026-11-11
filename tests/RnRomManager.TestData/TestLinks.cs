using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RnRomManager.TestData;

/// <summary>Links and special files for path-safety tests; each returns false where this OS or account can't make one.</summary>
public static class TestLinks
{
    /// <summary>A directory link: a symlink, or on Windows a junction (a reparse point any user may create).</summary>
    public static bool TryLinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>A file symlink. Windows needs Developer Mode or elevation for it.</summary>
    public static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A named pipe (Linux only): opening one for reading blocks until a writer appears.</summary>
    public static bool TryCreateFifo(string path) => OperatingSystem.IsLinux() && mkfifo(path, 0x1B6) == 0;

    /// <summary>Removes a directory link itself, never what it points at.</summary>
    public static void RemoveDirectoryLink(string link) => Directory.Delete(link, recursive: false);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
