using RnRomManager.Core.Discovery;

namespace RnRomManager.Data.Repositories;

/// <summary>
/// B2's one canonical normalization, shared by every discovery read, existence check and logo write.
/// MariaDB is the sole authority: the app never trims, lowercases or hashes names itself; it only compares the bytes
/// MariaDB returns.
/// </summary>
internal static class DiscoverySql
{
    /// <summary>The <c>games</c> column for <paramref name="kind"/> (a fixed name, never user input).</summary>
    public static string Column(DiscoveryKind kind) => kind switch
    {
        DiscoveryKind.Publishers => "publishers",
        DiscoveryKind.Genres => "genres",
        DiscoveryKind.Developers => "developers",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// CTEs <c>discovery_values(game_id, trimmed, norm)</c>: one row per non-blank string entry of every game's array.
    /// <c>JSON_TABLE</c> only numbers the entries; each complete value is extracted by ordinal, so no column width
    /// truncates it. <c>trimmed</c> drops leading and trailing Unicode whitespace; <c>norm</c> is its lowercase bytes
    /// under an explicit accent- and case-sensitive collation (the JSON columns are <c>utf8mb4_bin</c>, whose case
    /// mapping differs), compared as binary.
    /// </summary>
    public static string ValuesCte(DiscoveryKind kind)
    {
        var column = $"g.{Column(kind)}";
        var entry = $"JSON_EXTRACT({column}, CONCAT('$[', jt.ord - 1, ']'))";
        return $$"""
            discovery_entries AS (
                SELECT g.id AS game_id, {{Trimmed($"JSON_UNQUOTE({entry})")}} AS trimmed
                FROM games g, JSON_TABLE({{column}}, '$[*]' COLUMNS (ord FOR ORDINALITY)) jt
                WHERE JSON_TYPE({{entry}}) = 'STRING'
            ),
            discovery_values AS (
                SELECT game_id, trimmed, {{Norm("trimmed")}} AS norm
                FROM discovery_entries
                WHERE trimmed <> ''
            )
            """;
    }

    /// <summary><paramref name="text"/> converted to utf8mb4 without leading and trailing Unicode whitespace (<c>trimmed</c>).</summary>
    public static string Trimmed(string text) =>
        $$"""REGEXP_REPLACE(CONVERT({{text}} USING utf8mb4), '^[\\s\\p{Z}]+|[\\s\\p{Z}]+$', '')""";

    /// <summary>A trimmed value's <c>norm</c>: its lowercase bytes under an accent- and case-sensitive collation.</summary>
    public static string Norm(string trimmed) =>
        $"CAST(LOWER(CONVERT({trimmed} USING utf8mb4) COLLATE utf8mb4_uca1400_as_cs) AS BINARY)";

    /// <summary>The public key of a <c>norm</c>: lowercase hexadecimal SHA-256 of its bytes.</summary>
    public const string Key = "SHA2(norm, 256)";
}
