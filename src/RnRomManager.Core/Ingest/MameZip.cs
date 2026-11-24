using System.Globalization;
using System.IO.Compression;

namespace RnRomManager.Core.Ingest;

/// <summary>Arcade I2: a set ZIP's file list, read from its central directory; no entry is decompressed.</summary>
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
}
