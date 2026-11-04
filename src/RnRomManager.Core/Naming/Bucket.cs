namespace RnRomManager.Core.Naming;

/// <summary>Spec §5 / D7: the library letter folder of a ZIP name.</summary>
public static class Bucket
{
    public const string Other = "#";

    /// <summary>First character, upper-cased if it is an ASCII letter (<c>seaQuest DSV.zip</c> → <c>S</c>); anything else → <c>#</c>.</summary>
    public static string For(string zipName) =>
        zipName.Length > 0 && char.IsAsciiLetter(zipName[0]) ? char.ToUpperInvariant(zipName[0]).ToString() : Other;
}
