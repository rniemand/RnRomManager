using RnRomManager.Core.Media;

namespace RnRomManager.Core.Library;

/// <summary>Shared image rules of info.json v3 (I2–I4): naming, the cover, and what fetched media may change.</summary>
public static class GameImages
{
    public const int MaxImages = 20;

    public static string Prefix(ImageType type) => type == ImageType.Snap ? "snap" : "boxart";

    /// <summary>
    /// I2: <c>&lt;prefix&gt;-NN.&lt;ext&gt;</c> with the lowest NN (01–99) no existing name uses for that prefix, whatever its
    /// extension, compared ignoring case.
    /// </summary>
    /// <exception cref="InvalidOperationException">All 99 numbers are taken (can't happen within <see cref="MaxImages"/>).</exception>
    public static string NextName(IEnumerable<string> existing, ImageType type, string extension)
    {
        var prefix = Prefix(type);
        var stems = existing.Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; n <= 99; n++)
        {
            var stem = $"{prefix}-{n:00}";
            if (!stems.Contains(stem))
                return $"{stem}.{extension}";
        }
        throw new InvalidOperationException($"No free {prefix} image name.");
    }

    /// <summary>A new game's images (ingest, move to a new game): the box art (real or placeholder) is the cover.</summary>
    public static IReadOnlyList<InfoJsonImage> ForNewGame(MediaResult found, DateTimeOffset now) =>
        [found.BoxArt.ToInfoJson(now, isCover: true), .. found.Snap is { } snap ? [snap.ToInfoJson(now, isCover: false)] : Array.Empty<InfoJsonImage>()];

    /// <summary>The first image becomes the cover when none is (I3: the first image added to an empty game).</summary>
    public static IReadOnlyList<InfoJsonImage> EnsureCover(IReadOnlyList<InfoJsonImage> images) =>
        images.Count == 0 || images.Any(i => i.IsCover) ? images : [images[0] with { IsCover = true }, .. images.Skip(1)];

    /// <summary>
    /// I4a merge: the target keeps its cover; the source's non-default images are added under new names (I2, same type and
    /// extension); the source placeholder is dropped; a real source box art removes the target's placeholder, and if that
    /// was the cover the first such box art (by new name) is; a target with no cover gets the first image.
    /// </summary>
    /// <returns>The images plus, per copied image, its source name; null when the result exceeds <see cref="MaxImages"/>.</returns>
    public static (IReadOnlyList<InfoJsonImage> Images, IReadOnlyList<(InfoJsonImage Copy, string SourceName)> Copies)? Merge(
        IReadOnlyList<InfoJsonImage> into, IReadOnlyList<InfoJsonImage> from)
    {
        var images = into.ToList();
        var copies = new List<(InfoJsonImage Copy, string SourceName)>();
        foreach (var image in from.Where(i => !i.IsDefault))
        {
            var name = NextName(images.Select(i => i.FileName), image.Type, Path.GetExtension(image.FileName).TrimStart('.'));
            var copy = image with { FileName = name, IsCover = false };
            images.Add(copy);
            copies.Add((copy, image.FileName));
        }
        var boxArts = copies.Select(c => c.Copy).Where(i => i.Type == ImageType.BoxArt).ToList();
        if (images.FirstOrDefault(i => i.IsDefault) is { } placeholder && boxArts.Count > 0)
        {
            images.Remove(placeholder);
            if (placeholder.IsCover)
            {
                var cover = boxArts.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).First();
                images[images.IndexOf(cover)] = cover with { IsCover = true };
            }
        }
        return images.Count > MaxImages ? null : (EnsureCover(images), copies);
    }

    /// <summary>
    /// I4 for ingest into an existing game and media retry: fetched box art only replaces the default placeholder (keeping
    /// its number and cover status) or fills a game with no images at all; a fetched snap is added only when the game has
    /// no <c>Snap</c>. Uploaded and fetched non-default images are never touched. T8: every name takes the fetched image's
    /// own extension (<c>boxart-01.png</c> → <c>boxart-01.jpg</c>); a renamed placeholder that would collide, ignoring
    /// case, with another image gets the next free name instead.
    /// </summary>
    /// <returns>The new image list and the entries to write (name under <c>images/</c> → bytes); null when nothing changes.</returns>
    public static (IReadOnlyList<InfoJsonImage> Images, IReadOnlyDictionary<string, byte[]> Entries)? ApplyFetched(
        IReadOnlyList<InfoJsonImage> current, MediaResult found, DateTimeOffset now)
    {
        var images = current.ToList();
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var placeholder = images.FirstOrDefault(i => i.IsDefault);
        if (placeholder is not null && !found.BoxArt.IsDefault)
        {
            var others = images.Where(i => !ReferenceEquals(i, placeholder)).Select(i => i.FileName).ToList();
            var name = Path.ChangeExtension(placeholder.FileName, found.BoxArt.Extension);
            if (others.Contains(name, StringComparer.OrdinalIgnoreCase))
                name = NextName(others, ImageType.BoxArt, found.BoxArt.Extension);
            images[images.IndexOf(placeholder)] = found.BoxArt.ToInfoJson(now, placeholder.IsCover) with { FileName = name };
            entries[name] = found.BoxArt.Bytes;
        }
        else if (images.Count == 0)
        {
            images.Add(found.BoxArt.ToInfoJson(now, isCover: true));
            entries[found.BoxArt.FileName] = found.BoxArt.Bytes;
        }
        if (found.Snap is { } snap && !images.Any(i => i.Type == ImageType.Snap) && images.Count < MaxImages)
        {
            var name = NextName(images.Select(i => i.FileName), ImageType.Snap, snap.Extension);
            images.Add(snap.ToInfoJson(now, isCover: false) with { FileName = name });
            entries[name] = snap.Bytes;
        }
        return entries.Count == 0 ? null : (EnsureCover(images), entries);
    }
}
