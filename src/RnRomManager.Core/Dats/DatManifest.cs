using System.Text.Json;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Dats;

/// <summary>Reads and writes <c>dats.json</c>: a JSON array of <see cref="DatManifestEntry"/>.</summary>
public static class DatManifest
{
    public const string FileName = "dats.json";

    /// <summary>T3: the most a manifest may hold.</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <exception cref="JsonException">Not a valid manifest.</exception>
    public static IReadOnlyList<DatManifestEntry> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, path);
    }

    /// <param name="name">For messages.</param>
    /// <exception cref="JsonException">Not a valid manifest.</exception>
    public static IReadOnlyList<DatManifestEntry> Read(Stream stream, string name)
    {
        var entries = JsonSerializer.Deserialize<List<DatManifestEntry?>>(stream, Json)
            ?? throw new JsonException($"{name} is empty.");
        // T3: a row that can't be assigned to a system makes the whole manifest unusable.
        if (entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.File)))
            throw new JsonException($"{name} has a null row or one without a file.");
        return entries!;
    }

    /// <summary>T3: the manifest at <paramref name="path"/> below <paramref name="cacheRoot"/>, read through a checked handle.</summary>
    /// <exception cref="IOException">Missing, unsafe, changed while read, or over <paramref name="maxBytes"/>.</exception>
    /// <exception cref="JsonException">Not a valid manifest.</exception>
    public static IReadOnlyList<DatManifestEntry> ReadChecked(string cacheRoot, string path, long maxBytes = MaxBytes)
    {
        using var stream = new MemoryStream(ReadCheckedBytes(cacheRoot, path, maxBytes), writable: false);
        return Read(stream, path);
    }

    /// <summary>
    /// T3/G4: a regular file below <paramref name="root"/>, through one checked handle, at most
    /// <paramref name="maxBytes"/> actually read; the handle and the path must still be the file opened afterwards.
    /// </summary>
    /// <exception cref="IOException">Missing (<see cref="FileNotFoundException"/>), unsafe, unreadable, too big, or replaced.</exception>
    /// <param name="afterRead">Test seam: runs once the bytes are read, before the handle and path are re-checked.</param>
    public static byte[] ReadCheckedBytes(string root, string path, long maxBytes, Action<string>? afterRead = null)
    {
        // Delete sharing as for library reads: a refresh may swap the file meanwhile, which the re-check below catches.
        var opened = ExternalRomPath.OpenRead(root, path, FileShare.Read | FileShare.Delete);
        if (opened.Status == ExternalPathStatus.Missing)
            throw new FileNotFoundException($"{path} is missing.", path);
        if (opened.Status != ExternalPathStatus.Found)
            throw new IOException($"{path} can't be read safely: {opened.Details}");
        using var stream = opened.Stream!;
        if (opened.Snapshot!.Length > maxBytes)
            throw new IOException($"{path} is {opened.Snapshot.Length} bytes, over the {maxBytes}-byte limit.");

        // The length can still grow after the open: read one byte past the cap to notice.
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1 - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
                throw new IOException($"{path} grew past the {maxBytes}-byte limit while it was read.");
        }
        afterRead?.Invoke(path);
        if (ExternalRomPath.SnapshotOf(stream) != opened.Snapshot
            || ExternalRomPath.ClassifyFile(root, path) is not { Status: ExternalPathStatus.Found } after || after.Snapshot != opened.Snapshot)
            throw new IOException($"{path} changed or was replaced while it was read.");
        return buffer.ToArray();
    }

    public static void Write(string path, IEnumerable<DatManifestEntry> entries) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(entries.ToList(), Json));
}
