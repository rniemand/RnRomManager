using RnRomManager.Core.Discovery;

namespace RnRomManager.Data.Repositories;

/// <summary>
/// B2's one canonical normalization, shared by every discovery read, existence check and logo write.
/// MariaDB is the sole authority: the app never normalizes names itself.
/// </summary>
internal static class DiscoverySql
{
    /// <summary>The <c>games</c> column for <paramref name="kind"/> (a fixed name, never user input).</summary>
    public static string Column(DiscoveryKind kind) => kind switch
    {
        DiscoveryKind.Publishers => "publishers",
        DiscoveryKind.Genres => "genres",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// CTEs <c>discovery_values(game_id, trimmed, norm)</c>: one row per non-blank string entry of every game's array.
    /// <c>JSON_TABLE</c> only numbers the entries; each complete value is extracted by ordinal, so no column width
    /// truncates it. <c>trimmed</c> drops leading and trailing Unicode whitespace; <c>norm</c> is its lowercase bytes
    /// under an explicit accent- and case-sensitive collation (the JSON columns are <c>utf8mb4_bin</c>).
    /// </summary>
    public static string ValuesCte(DiscoveryKind kind)
    {
        var column = $"g.{Column(kind)}";
        var entry = $"JSON_EXTRACT({column}, CONCAT('$[', jt.ord - 1, ']'))";
        return $$"""
            discovery_entries AS (
                SELECT g.id AS game_id,
                       REGEXP_REPLACE(CONVERT(JSON_UNQUOTE({{entry}}) USING utf8mb4), '^[\\s\\p{Z}]+|[\\s\\p{Z}]+$', '') AS trimmed
                FROM games g, JSON_TABLE({{column}}, '$[*]' COLUMNS (ord FOR ORDINALITY)) jt
                WHERE JSON_TYPE({{entry}}) = 'STRING'
            ),
            discovery_values AS (
                SELECT game_id, trimmed,
                       CAST(LOWER(CONVERT(trimmed USING utf8mb4) COLLATE utf8mb4_uca1400_as_cs) AS BINARY) AS norm
                FROM discovery_entries
                WHERE trimmed <> ''
            )
            """;
    }

    /// <summary>The public key of a grouped <c>norm</c>.</summary>
    public const string Key = "SHA2(norm, 256)";

    /// <summary>The group label (binary-smallest trimmed spelling), cut to the display limit with an ellipsis.</summary>
    public static readonly string Name = $"""
        IF(CHAR_LENGTH(CONVERT(MIN(CAST(trimmed AS BINARY)) USING utf8mb4)) > {DiscoveryGroup.MaxNameLength},
           CONCAT(LEFT(CONVERT(MIN(CAST(trimmed AS BINARY)) USING utf8mb4), {DiscoveryGroup.MaxNameLength - 1}), '…'),
           CONVERT(MIN(CAST(trimmed AS BINARY)) USING utf8mb4))
        """;
}
