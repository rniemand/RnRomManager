using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>One release in <c>info.json</c> (spec §6.2 Rom).</summary>
public sealed record InfoJsonRom
{
    /// <summary>Entry name under <c>roms/</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>As found in ingest (the archive entry name when it came from an archive).</summary>
    public required string OriginalFileName { get; init; }

    public required string BaseTitle { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public string? Revision { get; init; }
    public required IReadOnlyList<string> Flags { get; init; }
    public required bool IsPrimary { get; init; }
    public required InfoJsonIdentification Identification { get; init; }

    /// <summary>Handler-specific header object (§8.2); null when unreadable.</summary>
    public JsonObject? Header { get; init; }

    public required InfoJsonRomMetadata Metadata { get; init; }
}
