using System.IO.Compression;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>
/// A library ZIP opened for reading (spec §6, §9.3). The file is opened <c>FileShare.Read | FileShare.Delete</c> so an
/// atomic replace (§6.1) still works while it's open (§3.1). Entries are only ever read through a
/// <see cref="LimitStream"/>, never extracted by name.
/// </summary>
public sealed class LibraryZip : IDisposable
{
    public const string InfoJsonEntry = "info.json";
    public const string RomsFolder = "roms/";
    public const string ImagesFolder = "images/";

    /// <summary>Release notes: <c>notes/&lt;rom fileName&gt;.md</c>, UTF-8 Markdown, not listed in <c>info.json</c>.</summary>
    public const string NotesFolder = "notes/";
    public const int MaxNoteBytes = 65536;

    private readonly ZipArchive _archive;

    private LibraryZip(string path, ZipArchive archive, InfoJsonReadResult? infoJson)
    {
        Path = path;
        _archive = archive;
        InfoJson = infoJson;
        EntryNames = archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).ToList();
    }

    public string Path { get; }

    /// <summary>null when the ZIP has no <c>info.json</c> (<c>InfoJsonMissing</c>).</summary>
    public InfoJsonReadResult? InfoJson { get; }

    /// <summary>Every file entry's full name (folder entries left out).</summary>
    public IReadOnlyList<string> EntryNames { get; }

    public static string RomEntry(string fileName) => RomsFolder + fileName;
    public static string ImageEntry(string fileName) => ImagesFolder + fileName;
    public static string NoteEntry(string romFileName) => NotesFolder + romFileName + ".md";

    /// <summary>Opens and reads <c>info.json</c> (≤ 1 MiB, actual bytes).</summary>
    /// <exception cref="InvalidDataException">Not a readable ZIP (<c>ZipUnreadable</c>).</exception>
    /// <exception cref="IOException">The file can't be opened.</exception>
    public static LibraryZip Open(string path, InfoJsonReader reader)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(file, ZipArchiveMode.Read);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        try
        {
            return new LibraryZip(path, archive, ReadInfoJson(archive, reader));
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an entry capped at <paramref name="maxBytes"/> actual bytes (<see cref="LimitExceededException"/> past it).
    /// Corrupt compressed data throws <see cref="InvalidDataException"/> while reading.
    /// </summary>
    /// <exception cref="FileNotFoundException">No such entry (<c>EntryMissing</c>).</exception>
    public Stream OpenEntry(string fullName, long maxBytes)
    {
        var entry = _archive.GetEntry(fullName) ?? throw new FileNotFoundException($"ZIP entry '{fullName}' not found.", fullName);
        return new LimitStream(entry.Open(), maxBytes);
    }

    public bool HasEntry(string fullName) => _archive.GetEntry(fullName) is not null;

    /// <summary>The entry's uncompressed size from the central directory, or null when there's no such entry.</summary>
    public long? EntryLength(string fullName) => _archive.GetEntry(fullName)?.Length;

    private static InfoJsonReadResult? ReadInfoJson(ZipArchive archive, InfoJsonReader reader)
    {
        var entry = archive.GetEntry(InfoJsonEntry);
        if (entry is null)
            return null;

        using var buffer = new MemoryStream();
        try
        {
            using var stream = new LimitStream(entry.Open(), InfoJsonReader.MaxBytes);
            stream.CopyTo(buffer);
        }
        catch (LimitExceededException)
        {
            return InfoJsonReadResult.Invalid($"info.json is larger than {InfoJsonReader.MaxBytes} bytes");
        }
        return reader.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    public void Dispose() => _archive.Dispose();
}
