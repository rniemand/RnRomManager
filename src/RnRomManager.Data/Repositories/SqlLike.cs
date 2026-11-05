namespace RnRomManager.Data.Repositories;

/// <summary><c>LIKE</c> patterns from user input (spec §14.2): <c>\</c>, <c>%</c> and <c>_</c> match literally.</summary>
internal static class SqlLike
{
    // MariaDB's default LIKE escape character is the backslash.
    public static string Escape(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    public static string Contains(string value) => "%" + Escape(value) + "%";
}
