using RnRomManager.Core.Library;

namespace RnRomManager.Core.Storage;

/// <summary>
/// The names taken in one library folder while a write allocates into it (spec E2), compared ignoring case: the
/// files and folders on disk, the external files the mirror says games in that folder own (on disk or not, so a
/// missing file's name isn't handed to another game), the names the opened destination ZIP lists, and everything
/// this write has allocated so far.
/// </summary>
public sealed class ExternalNameReservations
{
    private readonly HashSet<string> _taken;

    public ExternalNameReservations(IEnumerable<string> taken) => _taken = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">The folder on disk; a missing one has no entries.</param>
    /// <param name="folderPath">The same folder relative to <c>Paths:Roms</c> with <c>/</c> separators, e.g. <c>switch/L</c>.</param>
    /// <param name="claims">Mirrored external ROMs (<c>IRomRepository.ListExternalAsync</c>); those of other folders are ignored.</param>
    /// <param name="listedByDestination">The <c>externalFileName</c>s of the opened destination ZIP, when the mirror may lag behind it.</param>
    public static ExternalNameReservations ForFolder(
        string folder,
        string folderPath,
        IEnumerable<ExternalRomRow> claims,
        IEnumerable<string>? listedByDestination = null)
    {
        var onDisk = Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Select(e => Path.GetFileName(e)!) : [];
        var prefix = folderPath.TrimEnd('/') + "/";
        var claimed = claims
            .Where(c => c.ZipPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && c.ZipPath.IndexOf('/', prefix.Length) < 0)
            .Select(c => c.ExternalFileName);
        return new ExternalNameReservations([.. onDisk, .. claimed, .. listedByDestination ?? []]);
    }

    public bool IsTaken(string name) => _taken.Contains(name);

    public void Reserve(string name) => _taken.Add(name);

    /// <summary>Frees a name this write is about to vacate, e.g. the current name of a file it renames.</summary>
    public void Release(string name) => _taken.Remove(name);
}
