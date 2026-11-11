using System.IO.Compression;
using Microsoft.Extensions.Logging;
using RnRomManager.Core.Storage;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace RnRomManager.Core.Ingest;

/// <summary>One ROM entry extracted from an archive.</summary>
/// <param name="EntryPath">Full path inside the archive (<c>archive_entry</c>), <c>/</c>-separated.</param>
/// <param name="ExtractedPath">Where it was written under the work dir.</param>
public sealed record ExtractedEntry(string EntryPath, string ExtractedPath, long SizeBytes)
{
    /// <summary>The file-name part: <c>originalFileName</c> (§7.3).</summary>
    public string FileName => EntryPath[(EntryPath.LastIndexOf('/') + 1)..];
}

/// <summary>The whole archive is rejected (§7.3): a <c>Failed</c> reason code plus details.</summary>
public sealed class ArchiveRejectedException(string reason, string details) : Exception(details)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// An extracted copy couldn't be created, written or closed in the work dir: an <c>IoError</c> of this app's own
/// storage, not a fault of the archive.
/// </summary>
public sealed class ArchiveOutputException(string message, Exception inner) : Exception(message, inner);

/// <summary>§7.3 archive reason codes (spec §23.1).</summary>
public static class ArchiveFailures
{
    public const string ArchiveUnsafe = "ArchiveUnsafe";
    public const string ArchiveCorrupt = "ArchiveCorrupt";
    public const string ArchiveEncrypted = "ArchiveEncrypted";
}

/// <summary>
/// Unpacks a <c>.zip</c> (System.IO.Compression) or <c>.7z</c> (SharpCompress) into a work dir (spec §7.2 step 1a, §7.3).
/// Only entries with a ROM extension are extracted; other files and nested archives are skipped (logged at
/// <c>Info</c>). The whole archive is rejected on more than <see cref="MaxEntries"/> entries, an absolute or
/// traversing entry name, a symlink, or more actual bytes than allowed (per ROM entry: the handler's
/// <c>MaxRomSizeBytes</c>; in total: <see cref="MaxTotalBytes"/>). Sizes are counted on the bytes streamed, never
/// the declared sizes.
/// </summary>
public sealed class ArchiveExtractor(ILogger<ArchiveExtractor> logger)
{
    public const int DefaultMaxEntries = 100;
    public const long DefaultMaxTotalBytes = 256L * 1024 * 1024;

    public static readonly IReadOnlyList<string> Extensions = [".zip", ".7z"];

    public int MaxEntries { get; init; } = DefaultMaxEntries;
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;

    /// <summary>External systems: the longest ROM entry path and file name, in Unicode scalar values (the columns that store them).</summary>
    public const int MaxEntryPathScalars = 512;
    public const int MaxEntryNameScalars = 255;

    /// <summary>Creates one extracted copy for an external system; the file must not exist. A test seam.</summary>
    public Func<string, Stream> CreateOutput { get; init; } = path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    public static bool IsArchive(string fileName) => Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <param name="maxRomBytes">For an entry's extension: the ROM size limit, or null when it isn't a ROM extension.</param>
    /// <exception cref="ArchiveRejectedException">The whole archive is refused; the work dir may hold partial output.</exception>
    public IReadOnlyList<ExtractedEntry> Extract(string archivePath, string workDir, Func<string, long?> maxRomBytes) =>
        Guarded(() =>
        {
            if (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions());
                return ExtractAll(SevenZipEntries(archive), archivePath, workDir, maxRomBytes, external: false);
            }
            using var zip = ZipFile.OpenRead(archivePath);
            return ExtractAll(ZipEntries(zip), archivePath, workDir, maxRomBytes, external: false);
        });

    /// <summary>
    /// For an external system (spec I1): reads the archive from the stream the caller opened and checked, and
    /// never uses an entry's name as a file name. Each ROM entry is written to
    /// <c>&lt;index&gt;/entry&lt;extension&gt;</c> under <paramref name="workDir"/>, which the caller created and
    /// checked; <see cref="ExtractedEntry.EntryPath"/> keeps the entry's own path. Entry paths are checked as text,
    /// the same on every OS. Two ROM entries with the same path, or one too long to store, reject the archive
    /// before anything is written.
    /// </summary>
    /// <param name="archive">Seekable; left open.</param>
    /// <param name="archiveName">The archive's file name: its extension picks the reader, and it names the archive in logs.</param>
    /// <exception cref="ArchiveRejectedException">The whole archive is refused; the work dir may hold partial output.</exception>
    /// <exception cref="ArchiveOutputException">An extracted copy couldn't be written.</exception>
    public IReadOnlyList<ExtractedEntry> Extract(Stream archive, string archiveName, string workDir, Func<string, long?> maxRomBytes) =>
        Guarded(() =>
        {
            if (Path.GetExtension(archiveName).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var sevenZip = SevenZipArchive.OpenArchive(archive, new ReaderOptions { LeaveStreamOpen = true });
                return ExtractAll(SevenZipEntries(sevenZip), archiveName, workDir, maxRomBytes, external: true);
            }
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            return ExtractAll(ZipEntries(zip), archiveName, workDir, maxRomBytes, external: true);
        });

    private static IReadOnlyList<ExtractedEntry> Guarded(Func<IReadOnlyList<ExtractedEntry>> extract)
    {
        try
        {
            return extract();
        }
        catch (Exception ex) when (ex is ArchiveRejectedException or ArchiveOutputException)
        {
            throw;
        }
        catch (SharpCompress.Common.CryptographicException ex)
        {
            throw new ArchiveRejectedException(ArchiveFailures.ArchiveEncrypted, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException
            or IndexOutOfRangeException or ArgumentException
            || ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true)
        {
            throw new ArchiveRejectedException(ArchiveFailures.ArchiveCorrupt, ex.Message);
        }
    }

    private sealed record Entry(string Name, bool IsDirectory, bool IsEncrypted, bool IsLink, Func<Stream> Open);

    private IReadOnlyList<ExtractedEntry> ExtractAll(IEnumerable<Entry> entries, string archivePath, string workDir, Func<string, long?> maxRomBytes,
        bool external)
    {
        var files = entries.Where(e => !e.IsDirectory).ToList();
        if (files.Count > MaxEntries)
            throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe, $"{files.Count} entries; at most {MaxEntries} are allowed");

        // Every check that needs no data first, so an unsafe archive writes nothing.
        foreach (var entry in files)
        {
            var name = entry.Name.Replace('\\', '/');
            // External: the name is only text (never a path on disk), so it is judged the same way on every OS.
            var leaves = external
                ? name.StartsWith('/') || (name.Length > 1 && char.IsAsciiLetter(name[0]) && name[1] == ':') || name.Contains('\0')
                : name.StartsWith('/') || Path.IsPathRooted(entry.Name);
            if (leaves || name.Split('/').Contains(".."))
                throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe, $"entry '{entry.Name}' leaves the archive");
            try
            {
                if (!external)
                    SafePath.Resolve(workDir, name);
            }
            catch (UnsafePathException)
            {
                throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe, $"entry '{entry.Name}' leaves the archive");
            }
            if (entry.IsLink)
                throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe, $"entry '{entry.Name}' is a symbolic link");
            if (entry.IsEncrypted)
                throw new ArchiveRejectedException(ArchiveFailures.ArchiveEncrypted, $"entry '{entry.Name}' is encrypted");
        }

        if (external)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in files.Select(f => f.Name.Replace('\\', '/')))
            {
                var fileName = path[(path.LastIndexOf('/') + 1)..];
                if (IsArchive(fileName) || maxRomBytes(Path.GetExtension(fileName)) is null)
                    continue;
                if (ExternalRomName.Scalars(path) > MaxEntryPathScalars || ExternalRomName.Scalars(fileName) > MaxEntryNameScalars)
                {
                    throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe,
                        $"entry '{path}' is longer than {MaxEntryPathScalars} characters, or its name than {MaxEntryNameScalars}");
                }
                if (!seen.Add(path))
                    throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe, $"two entries are named '{path}'");
            }
        }

        var extracted = new List<ExtractedEntry>();
        long total = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var entryPath = files[i].Name.Replace('\\', '/');
            var fileName = entryPath[(entryPath.LastIndexOf('/') + 1)..];
            if (IsArchive(fileName) || maxRomBytes(Path.GetExtension(fileName)) is not { } maxRom)
            {
                logger.LogInformation("{Archive} › {Entry}: not a ROM (or a nested archive); skipped", archivePath, entryPath);
                continue;
            }

            // Each entry gets its own folder, so two entries with the same file name can't collide.
            var index = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var target = external
                ? Path.Combine(workDir, index, "entry" + Path.GetExtension(fileName).ToLowerInvariant())
                : SafePath.Resolve(workDir, Path.Combine(index, fileName));
            var limit = Math.Min(maxRom, MaxTotalBytes - total);
            long written;
            try
            {
                using var source = new LimitStream(files[i].Open(), limit);
                if (external)
                {
                    CopyToNewFile(source, target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var output = File.Create(target);
                    source.CopyTo(output);
                }
                written = source.BytesRead;
            }
            catch (LimitExceededException)
            {
                throw new ArchiveRejectedException(ArchiveFailures.ArchiveUnsafe, limit == maxRom
                    ? $"entry '{entryPath}' is larger than {maxRom} bytes"
                    : $"more than {MaxTotalBytes} bytes in total");
            }
            total += written;
            extracted.Add(new ExtractedEntry(entryPath, target, written));
        }
        return extracted;
    }

    /// <summary>Reads the archive and writes the copy separately, so a failure of the copy isn't taken for a bad archive.</summary>
    private void CopyToNewFile(Stream source, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = CreateOutput(target);
            var buffer = new byte[81920];
            while (true)
            {
                int read;
                try
                {
                    read = source.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    throw new SourceReadException(ex);
                }
                if (read == 0)
                    break;
                output.Write(buffer, 0, read);
            }
        }
        catch (SourceReadException ex)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveOutputException($"{target} can't be written: {ex.Message}", ex);
        }
    }

    private sealed class SourceReadException(Exception inner) : Exception(inner.Message, inner);

    private static IEnumerable<Entry> ZipEntries(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            // Unix mode in the high 16 bits of the external attributes; 0xA000 = S_IFLNK.
            var isLink = ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000;
            yield return new Entry(e.FullName, e.FullName.EndsWith('/'), e.IsEncrypted, isLink, e.Open);
        }
    }

    private static IEnumerable<Entry> SevenZipEntries(IArchive archive)
    {
        foreach (var e in archive.Entries)
        {
            // 7z attributes: Windows reparse point 0x400, or the Unix extension (0x8000) with S_IFLNK in the high word.
            var attrib = e.Attrib ?? 0;
            var isLink = !string.IsNullOrEmpty(e.LinkTarget) || (attrib & 0x400) != 0
                || ((attrib & 0x8000) != 0 && ((attrib >> 16) & 0xF000) == 0xA000);
            yield return new Entry(e.Key ?? "", e.IsDirectory, e.IsEncrypted, isLink, e.OpenEntryStream);
        }
    }
}
