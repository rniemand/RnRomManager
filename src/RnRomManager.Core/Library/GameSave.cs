using System.Text.Json.Serialization;

namespace RnRomManager.Core.Library;

/// <summary>
/// B2: one record of <c>saves/saves.json</c>. Its bytes are the entry <c>saves/&lt;owner&gt;/&lt;id&gt;&lt;extension&gt;</c>
/// (release editing B16, version 2), or <c>saves/&lt;id&gt;&lt;extension&gt;</c> for a record of a version 1 index.
/// </summary>
public sealed record GameSave
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary><see cref="GameSaves.KindState"/> or <see cref="GameSaves.KindBattery"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The release's <c>fileName</c> in <c>info.json</c> (B4: matched ignoring case; none matching is an orphan).</summary>
    public required string Release { get; init; }

    /// <summary>B1: empty, or <c>.</c> plus 1–8 of <c>[a-z0-9]</c>.</summary>
    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }
    public required string Sha1 { get; init; }
    public required string? UploadedBy { get; init; }
    public required DateTimeOffset UploadedAt { get; init; }
    public required string? UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>B17: read from a version 1 <c>saves.json</c>, whose entries sit directly in <c>saves/</c>.</summary>
    [JsonIgnore]
    public bool Legacy { get; init; }

    /// <summary>The ZIP entry holding the bytes (B16: in the owner's folder, <c>uploadedBy</c> lowercased).</summary>
    [JsonIgnore]
    public string Entry => LibraryZip.SaveEntry(Legacy ? Id + Extension : $"{UploadedBy?.ToLowerInvariant()}/{Id}{Extension}");
}
