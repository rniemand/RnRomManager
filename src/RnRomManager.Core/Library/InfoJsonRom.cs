using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>One release in <c>info.json</c> (spec §6.2 Rom).</summary>
public sealed record InfoJsonRom
{
    /// <summary>Entry name under <c>roms/</c>; for an external ROM, its logical name (download name, note name).</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// v4: for a system that stores ROMs outside the ZIP, the ROM's file in the ZIP's own folder (a plain file
    /// name); null for a ROM that is an entry of the ZIP.
    /// </summary>
    public string? ExternalFileName { get; init; }

    /// <summary>As found in ingest (the archive entry name when it came from an archive).</summary>
    public required string OriginalFileName { get; init; }

    public required string BaseTitle { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public string? Revision { get; init; }

    /// <summary>v5: an update's dotted version (<c>1.0.1</c>) from its file name; null otherwise.</summary>
    public string? Version { get; init; }

    public required IReadOnlyList<string> Flags { get; init; }
    public required bool IsPrimary { get; init; }
    public required InfoJsonIdentification Identification { get; init; }

    /// <summary>Handler-specific header object (§8.2); null when unreadable.</summary>
    public JsonObject? Header { get; init; }

    public required InfoJsonRomMetadata Metadata { get; init; }

    /// <summary>
    /// v6 (B12): null for a one-file release; otherwise the cue first, then the cue's other <c>FILE</c> names in order,
    /// without the data track (this rom's own file). Only <c>FileSets</c> systems may have it.
    /// </summary>
    public IReadOnlyList<InfoJsonRomFile>? Files { get; init; }
}
