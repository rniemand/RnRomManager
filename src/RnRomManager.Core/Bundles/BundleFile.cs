using System.Text;

namespace RnRomManager.Core.Bundles;

/// <summary>Structural rules for identities and labels (B3).</summary>
public static class BundleFile
{
    /// <summary>Labels and file names are capped at <c>VARCHAR(255)</c>'s length in code points.</summary>
    public const int MaxTextLength = 255;

    /// <summary>The most identities one resolution may carry (B5), the same as a queue's cap.</summary>
    public const int MaxBatch = BundleQueueLimits.MaxEntries;

    /// <summary>Exactly 40 lowercase hexadecimal characters.</summary>
    public static bool IsSha1(string? value)
    {
        if (value is not { Length: 40 }) return false;
        foreach (var c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>The length in Unicode code points, or -1 when the string holds an unpaired surrogate.</summary>
    public static int CodePoints(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return -1;
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return -1;
            }
            count++;
        }
        return count;
    }

    /// <summary>True for a non-empty, valid UTF-16 string of at most <paramref name="max"/> code points.</summary>
    public static bool IsText(string? value, int max = MaxTextLength) =>
        value is { Length: > 0 } && CodePoints(value) is > 0 and var n && n <= max;

    /// <summary>
    /// The first structural problem with an identity, as a field-relative message, or null when it's well formed. A
    /// well-formed identity may still be unresolved: that is checked against the library, not here.
    /// </summary>
    public static string? Validate(BundleFileIdentity? identity) => identity switch
    {
        null => "is required.",
        { GameId: var g } when g == Guid.Empty => "gameId must be a nonzero GUID.",
        { ReleaseSha1: var s } when !IsSha1(s) => "releaseSha1 must be 40 lowercase hexadecimal characters.",
        { Member: { } m } when !IsSha1(m.Sha1) => "member.sha1 must be 40 lowercase hexadecimal characters.",
        { Member: { } m } when !IsText(m.FileName) => $"member.fileName must be 1–{MaxTextLength} characters.",
        _ => null,
    };

    /// <summary>The key two identities share exactly when they name the same file (B1: duplicates are rejected).</summary>
    public static string Key(BundleFileIdentity identity)
    {
        var key = new StringBuilder().Append(identity.GameId.ToString("N")).Append('/').Append(identity.ReleaseSha1);
        if (identity.Member is { } m) key.Append('/').Append(m.Sha1).Append('/').Append(m.FileName);
        return key.ToString();
    }
}
