namespace RnRomManager.Core.Library;

/// <summary>A row of <c>roms</c> (spec §14.1).</summary>
public sealed record RomRow
{
    public long Id { get; init; }
    public required Guid GameId { get; init; }
    public required string SystemId { get; init; }
    public required string FileName { get; init; }
    public required string OriginalFileName { get; init; }
    public required string BaseTitle { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required IReadOnlyList<string> Flags { get; init; }

    /// <summary><c>|USA|Europe|</c>: pipe at both ends, so <c>LIKE '%|USA|%'</c> matches whole regions.</summary>
    public required string RegionKey { get; init; }

    public string? Revision { get; init; }
    public bool IsPrimary { get; init; }
    public required IdentificationMethod IdMethod { get; init; }
    public string? DatName { get; init; }
    public string? DatVersion { get; init; }
    public string? DatGameName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }

    /// <summary>The header object as JSON text; null when unreadable.</summary>
    public string? Header { get; init; }

    public required DateTime DateAdded { get; init; }
    public required DateTime DateModified { get; init; }

    /// <summary>
    /// The ROM's file next to the ZIP (name only, in the ZIP's folder) for a system that stores ROMs outside
    /// the ZIP; null when the ROM is an entry of the ZIP.
    /// </summary>
    public string? ExternalFileName { get; init; }

    /// <summary>An update's dotted version (info.json <c>version</c>); null otherwise.</summary>
    public string? Version { get; init; }
}
