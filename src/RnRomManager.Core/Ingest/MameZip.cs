using System.Globalization;
using System.IO.Compression;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace RnRomManager.Core.Ingest;

/// <summary>Arcade I2: a set ZIP's or 7z's file list, read from its directory; no entry is decompressed.</summary>
public static class MameZip
{
    public const int MaxEntries = 4096;

    /// <returns>
    /// Each non-directory entry's file name (after the last <c>/</c>) and CRC32 as 8 lower-case hex digits, in ZIP order;
    /// or, for a file that can't be used, the <c>InvalidHeader</c> details.
    /// </returns>
    public static (IReadOnlyList<MameEntry>? Entries, string? Error) ReadEntries(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException e)
        {
            return (null, $"not a readable ZIP: {e.Message}");
        }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
                return (null, $"more than {MaxEntries} files in the set ZIP");
            return (zip.Entries
                .Select(e => (Name: e.FullName[(e.FullName.LastIndexOf('/') + 1)..], e.Crc32))
                .Where(e => e.Name.Length > 0) // folders
                .Select(e => new MameEntry(e.Name, e.Crc32.ToString("x8", CultureInfo.InvariantCulture)))
                .ToList(), null);
        }
    }

    /// <summary>Arcade (FinalBurn Neo) I2: <see cref="ReadEntries"/> for a <c>.7z</c> set, from its headers only.</summary>
    /// <exception cref="IOException">A read failed (other than a truncated archive, which is an error result).</exception>
    public static (IReadOnlyList<MameEntry>? Entries, string? Error) ReadSevenZipEntries(Stream stream)
    {
        try
        {
            using var archive = SevenZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            if (entries.Any(e => e.IsEncrypted))
                return (null, "not a readable 7z: encrypted");
            if (entries.Count > MaxEntries)
                return (null, $"more than {MaxEntries} files in the set 7z");
            return (entries
                .Select(e => (Name: e.Key![(e.Key!.LastIndexOf('/') + 1)..], e.Crc))
                .Where(e => e.Name.Length > 0)
                .Select(e => new MameEntry(e.Name, ((uint)e.Crc).ToString("x8", CultureInfo.InvariantCulture)))
                .ToList(), null);
        }
        // Only a truncated archive among IOExceptions is a bad 7z; any other read failure stays IoError.
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or InvalidOperationException or NotSupportedException
            or IndexOutOfRangeException or ArgumentException or SharpCompress.Common.CryptographicException
            || ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true)
        {
            return (null, $"not a readable 7z: {ex.Message}");
        }
    }
}
