using System.Globalization;

namespace RnRomManager.Core.Naming;

/// <summary>
/// Spec §6.1.1 revision order: none &lt; any; dot-separated parts compared left to right, digits numerically and
/// letters ordinally (<c>Rev 2</c> &gt; <c>Rev 1</c>, <c>v1.10</c> &gt; <c>v1.9</c>, <c>Rev B</c> &gt; <c>Rev A</c>).
/// </summary>
public sealed class RevisionComparer : IComparer<string?>
{
    public static RevisionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return (x is null ? 0 : 1) - (y is null ? 0 : 1);

        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var result = ComparePart(a[i], b[i]);
            if (result != 0)
                return result;
        }
        // v1.0 < v1.0.1
        return a.Length.CompareTo(b.Length);
    }

    private static string[] Parts(string revision) =>
        (revision.StartsWith("Rev ", StringComparison.Ordinal) ? revision[4..]
            : revision.StartsWith('v') ? revision[1..]
            : revision).Split('.');

    /// <summary>Leading digits numerically, then the rest ordinally: "10" &gt; "9", "B" &gt; "A", "3a" &gt; "3".</summary>
    private static int ComparePart(string x, string y)
    {
        var (xNumber, xSuffix) = Split(x);
        var (yNumber, ySuffix) = Split(y);
        if (xNumber is not null && yNumber is not null)
        {
            var result = xNumber.Value.CompareTo(yNumber.Value);
            return result != 0 ? result : string.CompareOrdinal(xSuffix, ySuffix);
        }
        return string.CompareOrdinal(x, y);
    }

    private static (System.Numerics.BigInteger? Number, string Suffix) Split(string part)
    {
        var digits = part.TakeWhile(char.IsAsciiDigit).Count();
        return digits == 0
            ? (null, part)
            : (System.Numerics.BigInteger.Parse(part[..digits], CultureInfo.InvariantCulture), part[digits..]);
    }
}
