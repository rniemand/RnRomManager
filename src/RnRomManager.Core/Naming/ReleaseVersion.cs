using System.Text.RegularExpressions;

namespace RnRomManager.Core.Naming;

/// <summary>A release's dotted version, read from a <c>[x.y.z]</c> tag in its file name (Switch updates).</summary>
public static partial class ReleaseVersion
{
    /// <summary>The flag that marks a release as an update; only updates get a version.</summary>
    public const string UpdateFlag = "Update";

    /// <summary>A whole version: 2–4 dot-separated runs of 1–9 ASCII digits (at most 39 chars).</summary>
    [GeneratedRegex(@"\A[0-9]{1,9}(\.[0-9]{1,9}){1,3}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Valid();

    [GeneratedRegex(@"\[([0-9]{1,9}(?:\.[0-9]{1,9}){1,3})\]", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    /// <returns>The first <c>[x.y(.z(.w))]</c> tag's version, or null.</returns>
    public static string? From(string fileName) => Tag().Match(fileName) is { Success: true } m ? m.Groups[1].Value : null;

    /// <returns>The version a new release gets: <see cref="From"/> for an update, else null.</returns>
    public static string? For(IEnumerable<string> flags, string originalFileName) =>
        flags.Contains(UpdateFlag, StringComparer.Ordinal) ? From(originalFileName) : null;
}
