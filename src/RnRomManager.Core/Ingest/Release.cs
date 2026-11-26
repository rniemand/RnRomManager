using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Ingest;

/// <summary>What a DAT match names a release: from a libretro DAT entry (by SHA1 or Wii game ID) or a titledb entry (by title id).</summary>
/// <param name="FileName">Before E3 budgeting for an external system.</param>
internal sealed record Release(
    string FileName,
    string BaseTitle,
    string GameName,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    string? Revision,
    IReadOnlyList<string> Flags,
    IdentificationMethod Method,
    DatMetadata Metadata,
    string? TitleId);
