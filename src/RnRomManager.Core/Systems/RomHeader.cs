namespace RnRomManager.Core.Systems;

/// <summary>What a system handler read from a ROM's header (spec §22).</summary>
/// <param name="ClassifiedSystemId">Which handler <c>Id</c> the header says (e.g. <c>gb</c> / <c>gbc</c>).</param>
/// <param name="Title">Header title; feeds suggestions only, never naming (§8.4).</param>
/// <param name="IsPlausible">false → <c>InvalidHeader</c> when the ROM has no DAT match (§7.2 step 5).</param>
/// <param name="Fields">Serialised to info.json <c>header</c> / <c>roms.header</c>; JSON primitives only.</param>
public sealed record RomHeader(
    string? ClassifiedSystemId,
    string? Title,
    bool IsPlausible,
    IReadOnlyDictionary<string, object?> Fields);
