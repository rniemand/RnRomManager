using System.Text;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>
/// The §6.2 validation rules that the JSON shape can't express, in a fixed order; the first failure is reported.
/// Required/typed fields are already enforced by deserialization. No images is not invalid: that's the integrity issue
/// <c>MissingBoxArt</c>.
/// </summary>
public static class InfoJsonValidator
{
    /// <summary>Longest <c>externalFileName</c>, in UTF-8 bytes: one path component on ext4.</summary>
    public const int MaxExternalFileNameBytes = 255;

    /// <summary>Longest external rom <c>fileName</c>, in Unicode scalar values: the <c>roms.file_name</c> column.</summary>
    public const int MaxExternalRomFileNameScalars = 255;

    /// <param name="systems">Registered handlers by id: the game's handler gives the storage mode, ROM extensions and size limit.</param>
    /// <returns>null when valid, else the first failing rule.</returns>
    public static string? Validate(InfoJsonGame game, IReadOnlyDictionary<string, ISystemHandler> systems)
    {
        // A JSON null inside either list deserializes to a null element; report it before anything dereferences one.
        if (game.Roms.Any(r => r is null))
            return "roms must not contain null";
        if (game.Images.Any(i => i is null))
            return "images must not contain null";
        if (game.GameId == Guid.Empty)
            return "gameId must be a non-empty GUID";
        if (!systems.TryGetValue(game.System, out var handler))
            return $"system '{game.System}' is not a registered system";
        if (game.Rating is < 1 or > 5)
            return "rating must be null or 1-5";
        if (game.Roms.Count == 0)
            return "roms must not be empty";
        if (game.Roms.Count(r => r.IsPrimary) != 1)
            return "exactly one rom must be isPrimary";
        if (FileNameProblem(game.Roms.Select(r => r.FileName), "roms") is { } romNames)
            return romNames;
        if (FileNameProblem(game.Images.Select(i => i.FileName), "images") is { } imageNames)
            return imageNames;
        if (ExternalProblem(game, handler) is { } external)
            return external;

        foreach (var rom in game.Roms)
        {
            var m = rom.Metadata;
            if (!IsHex(m.Crc32, 8) || !IsHex(m.Md5, 32) || !IsHex(m.Sha1, 40))
                return $"rom '{rom.FileName}' hashes must be lowercase hex of the right length";
            if (m.SizeBytes < 0)
                return $"rom '{rom.FileName}' sizeBytes must not be negative";
            if (rom.Version is { } version && !ReleaseVersion.Valid().IsMatch(version))
                return $"rom '{rom.FileName}' version '{version}' is not a dotted version";
        }

        foreach (var image in game.Images)
        {
            if (!IsHex(image.Sha1, 40))
                return $"image '{image.FileName}' sha1 must be lowercase hex";
            if (image.Source is not (InfoJsonImage.SourceLibretro or InfoJsonImage.SourceDefault or InfoJsonImage.SourceUpload or InfoJsonImage.SourceTitleDb))
                return $"image '{image.FileName}' source must be '{InfoJsonImage.SourceLibretro}', '{InfoJsonImage.SourceDefault}', '{InfoJsonImage.SourceUpload}' or '{InfoJsonImage.SourceTitleDb}'";
        }

        if (game.Images.Count > GameImages.MaxImages)
            return $"at most {GameImages.MaxImages} images";
        if (game.Images.Count > 0 && game.Images.Count(i => i.IsCover) != 1)
            return "exactly one image must be isCover";
        var defaults = game.Images.Where(i => i.IsDefault).ToList();
        if (defaults.Count > 1)
            return "at most one isDefault image";
        if (defaults.Count == 1 && defaults[0].Type != ImageType.BoxArt)
            return "the isDefault image must be a BoxArt";
        return null;
    }

    /// <summary>
    /// The storage rules (v4). An embedded system's roms have no <c>externalFileName</c>. An external system's roms
    /// each have one, and both it and the logical <c>fileName</c> must be usable as a file name on every OS, since
    /// downloads, moves and deletes act on them.
    /// </summary>
    private static string? ExternalProblem(InfoJsonGame game, ISystemHandler handler)
    {
        if (handler.EmbedRomsInZip)
        {
            return game.Roms.FirstOrDefault(r => r.ExternalFileName is not null) is { } stray
                ? $"rom '{stray.FileName}' externalFileName must be null: system '{game.System}' stores ROMs in the ZIP"
                : null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rom in game.Roms)
        {
            if (rom.ExternalFileName is not { } pointer)
                return $"rom '{rom.FileName}' externalFileName is required: system '{game.System}' stores ROMs outside the ZIP";

            // Unlike a rom fileName, ".." inside the name is fine: the ZIP base it starts with may hold "...".
            if (pointer is "." or ".." || PortableNameProblem(pointer) is not null)
                return $"rom '{rom.FileName}' externalFileName must be a plain, portable file name";
            if (Encoding.UTF8.GetByteCount(pointer) > MaxExternalFileNameBytes)
                return $"rom '{rom.FileName}' externalFileName must be at most {MaxExternalFileNameBytes} UTF-8 bytes";
            if (!HasRomExtension(pointer, handler))
                return $"rom '{rom.FileName}' externalFileName must end with a ROM extension of system '{game.System}'";
            if (!seen.Add(pointer))
                return $"rom '{rom.FileName}' externalFileName is not unique (case-insensitive)";

            if (PortableNameProblem(rom.FileName) is { } problem)
                return $"rom fileName '{rom.FileName}' {problem}";
            if (rom.FileName.EnumerateRunes().Count() > MaxExternalRomFileNameScalars)
                return $"rom fileName '{rom.FileName}' must be at most {MaxExternalRomFileNameScalars} characters";
            if (!HasRomExtension(rom.FileName, handler))
                return $"rom fileName '{rom.FileName}' must end with a ROM extension of system '{game.System}'";
            if (rom.Metadata.SizeBytes > handler.MaxRomSizeBytes)
                return $"rom '{rom.FileName}' sizeBytes is over the system's limit of {handler.MaxRomSizeBytes}";
        }
        return null;
    }

    /// <returns>null when the name can be a file name on every OS; else what is wrong with it.</returns>
    internal static string? PortableNameProblem(string name)
    {
        if (name.Length == 0)
            return "must not be empty";
        foreach (var c in name)
        {
            if (c is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*' || char.IsControl(c))
                return "must not contain / \\ < > : \" | ? * or a control character";
        }
        return FileNameSanitizer.IsReservedDeviceName(name) ? "must not be a Windows device name" : null;
    }

    private static bool HasRomExtension(string name, ISystemHandler handler) =>
        handler.RomExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string? FileNameProblem(IEnumerable<string> names, string list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
                return $"{list} fileName '{name}' must be a plain name without '/', '\\' or '..'";
            if (!seen.Add(name))
                return $"{list} fileName '{name}' is not unique (case-insensitive)";
        }
        return null;
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
