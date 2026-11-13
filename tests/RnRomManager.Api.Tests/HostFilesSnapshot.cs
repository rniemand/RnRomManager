namespace RnRomManager.Api.Tests;

/// <summary>
/// The folders and files under a shared test host's root right after startup, and a way back to them:
/// anything a test added is deleted (links themselves, never their targets), anything it removed is recreated, and
/// every recorded file gets its recorded bytes again.
/// </summary>
internal sealed class HostFilesSnapshot
{
    private readonly string _root;
    private readonly HashSet<string> _skipped;
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    private HostFilesSnapshot(string root, IEnumerable<string> skipped)
    {
        _root = Path.GetFullPath(root);
        _skipped = skipped.Select(name => Path.Combine(_root, name)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Records <paramref name="root"/>, leaving out the top-level entries named in <paramref name="skip"/>.</summary>
    public static HostFilesSnapshot Take(string root, params string[] skip)
    {
        var snapshot = new HostFilesSnapshot(root, skip);
        snapshot.Record(new DirectoryInfo(snapshot._root));
        return snapshot;
    }

    private void Record(DirectoryInfo directory)
    {
        _directories.Add(directory.FullName);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (_skipped.Contains(entry.FullName))
                continue;
            if (entry is DirectoryInfo child)
                Record(child);
            else
                _files[entry.FullName] = File.ReadAllBytes(entry.FullName);
        }
    }

    public void Restore()
    {
        Prune(new DirectoryInfo(_root));
        foreach (var directory in _directories.Order(StringComparer.Ordinal))
            Directory.CreateDirectory(directory);
        foreach (var (path, bytes) in _files)
            File.WriteAllBytes(path, bytes);
    }

    /// <summary>Deletes what isn't in the snapshot below <paramref name="directory"/>.</summary>
    private void Prune(DirectoryInfo directory)
    {
        MakeAccessible(directory);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (_skipped.Contains(entry.FullName))
                continue;
            var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null;
            if (entry is DirectoryInfo child && !isLink)
            {
                if (_directories.Contains(child.FullName))
                    Prune(child);
                else
                    DeleteTree(child);
            }
            else if (isLink || !_files.ContainsKey(entry.FullName))
            {
                Delete(entry);
            }
        }
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        MakeAccessible(directory);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null;
            if (entry is DirectoryInfo child && !isLink)
                DeleteTree(child);
            else
                Delete(entry);
        }
        Delete(directory);
    }

    /// <summary>Deletes one file, link or empty folder; a link is removed without touching what it points at.</summary>
    private static void Delete(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            entry.Attributes &= ~FileAttributes.ReadOnly;
        if (entry is DirectoryInfo directory)
            directory.Delete(recursive: false);
        else
            entry.Delete();
    }

    /// <summary>Tests take permissions away (unreadable folders); give them back so the folder can be emptied.</summary>
    private static void MakeAccessible(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
            return;
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if ((directory.UnixFileMode & owner) != owner)
            directory.UnixFileMode |= owner;
    }
}
