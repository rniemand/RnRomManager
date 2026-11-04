namespace RnRomManager.Core.Library;

/// <summary>
/// The §6.2 validation rules that the JSON shape can't express, in a fixed order; the first failure is reported.
/// Required/typed fields are already enforced by deserialization. No <c>BoxArt</c> is not invalid: that's the
/// integrity issue <c>MissingBoxArt</c>.
/// </summary>
public static class InfoJsonValidator
{
    /// <param name="systemIds">Registered handler ids.</param>
    /// <returns>null when valid, else the first failing rule.</returns>
    public static string? Validate(InfoJsonGame game, IReadOnlySet<string> systemIds)
    {
        if (game.GameId == Guid.Empty)
            return "gameId must be a non-empty GUID";
        if (!systemIds.Contains(game.System))
            return $"system '{game.System}' is not a registered system";
        if (game.Roms.Count == 0)
            return "roms must not be empty";
        if (game.Roms.Count(r => r.IsPrimary) != 1)
            return "exactly one rom must be isPrimary";
        if (FileNameProblem(game.Roms.Select(r => r.FileName), "roms") is { } romNames)
            return romNames;
        if (FileNameProblem(game.Images.Select(i => i.FileName), "images") is { } imageNames)
            return imageNames;

        foreach (var rom in game.Roms)
        {
            var m = rom.Metadata;
            if (!IsHex(m.Crc32, 8) || !IsHex(m.Md5, 32) || !IsHex(m.Sha1, 40))
                return $"rom '{rom.FileName}' hashes must be lowercase hex of the right length";
            if (m.SizeBytes < 0)
                return $"rom '{rom.FileName}' sizeBytes must not be negative";
        }

        foreach (var image in game.Images)
        {
            if (!IsHex(image.Sha1, 40))
                return $"image '{image.FileName}' sha1 must be lowercase hex";
            if (image.Source is not (InfoJsonImage.SourceLibretro or InfoJsonImage.SourceDefault))
                return $"image '{image.FileName}' source must be '{InfoJsonImage.SourceLibretro}' or '{InfoJsonImage.SourceDefault}'";
        }

        if (game.Images.Count(i => i.Type == ImageType.BoxArt) > 1)
            return "at most one BoxArt image";
        if (game.Images.Count(i => i.Type == ImageType.Snap) > 1)
            return "at most one Snap image";
        return null;
    }

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
