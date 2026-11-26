namespace RnRomManager.Core.Bundles;

/// <summary>
/// Structural validation shared by queue create and replace (B2–B4). It never consults the library: a well-formed
/// identity is accepted whether or not it currently resolves, so users can resave queues after a library change.
/// </summary>
public static class BundleQueueValidation
{
    /// <summary>Trims the name; null when it is 1–<see cref="BundleQueueLimits.MaxNameLength"/> code points, else the error.</summary>
    public static string? ValidateName(ref string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "A name is required.";
        return BundleFile.IsText(name, BundleQueueLimits.MaxNameLength)
            ? null
            : $"The name must be at most {BundleQueueLimits.MaxNameLength} characters.";
    }

    /// <summary>
    /// The validated entries in order, or the field errors (<c>entries</c>, <c>entries[i].gameId</c>, …). Duplicate
    /// identities are rejected; a different file with the same display name is allowed (B1).
    /// </summary>
    public static (IReadOnlyList<BundleEntry> Entries, Dictionary<string, string[]> Errors) ValidateEntries(
        IReadOnlyList<BundleEntryInput?>? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors["entries"] = ["An array of entries is required."];
            return ([], errors);
        }
        if (input.Count > BundleQueueLimits.MaxEntries)
        {
            errors["entries"] = [$"At most {BundleQueueLimits.MaxEntries} files."];
            return ([], errors);
        }

        var entries = new List<BundleEntry>(input.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < input.Count; i++)
        {
            var field = $"entries[{i}]";
            if (input[i] is not { } item)
            {
                errors[field] = ["An entry is required."];
                continue;
            }
            if (ParseIdentity(item.GameId, item.ReleaseSha1, item.Member, out var identity) is { } identityError)
            {
                errors[$"{field}.{identityError.Field}"] = [identityError.Message];
                continue;
            }
            if (!BundleFile.IsText(item.GameTitle))
                errors[$"{field}.gameTitle"] = [$"The game title must be 1–{BundleFile.MaxTextLength} characters."];
            if (!BundleFile.IsText(item.FileName))
                errors[$"{field}.fileName"] = [$"The file name must be 1–{BundleFile.MaxTextLength} characters."];
            if (!seen.TryAdd(BundleFile.Key(identity!), i))
                errors[field] = [$"The same file as entries[{seen[BundleFile.Key(identity!)]}]."];
            if (errors.Count == 0)
                entries.Add(new BundleEntry(identity!.GameId, identity.ReleaseSha1, identity.Member, item.GameTitle!, item.FileName!));
        }

        return errors.Count == 0 ? (entries, errors) : ([], errors);
    }

    /// <summary>
    /// Parses and checks one identity (B3); on failure returns the field (<c>gameId</c>, <c>releaseSha1</c>,
    /// <c>member.sha1</c>, <c>member.fileName</c>) and its message. GUIDs use the hyphenated form.
    /// </summary>
    public static (string Field, string Message)? ParseIdentity(string? gameId, string? releaseSha1, BundleMemberInput? member,
        out BundleFileIdentity? identity)
    {
        identity = null;
        if (!Guid.TryParseExact(gameId, "D", out var game) || game == Guid.Empty)
            return ("gameId", "Must be a nonzero GUID.");
        if (!BundleFile.IsSha1(releaseSha1))
            return ("releaseSha1", "Must be 40 lowercase hexadecimal characters.");
        if (member is not null)
        {
            if (!BundleFile.IsSha1(member.Sha1))
                return ("member.sha1", "Must be 40 lowercase hexadecimal characters.");
            if (!BundleFile.IsText(member.FileName))
                return ("member.fileName", $"Must be 1–{BundleFile.MaxTextLength} characters.");
        }

        identity = new BundleFileIdentity(game, releaseSha1!, member is null ? null : new BundleMember(member.Sha1!, member.FileName!));
        return null;
    }
}
