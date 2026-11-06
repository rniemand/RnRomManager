using System.Security.Cryptography;
using System.Text;

namespace RnRomManager.Core.Reports;

/// <summary>RP6/RP7: the duplicate-group membership key.</summary>
public static class DuplicateGroups
{
    /// <summary>
    /// Lower-case SHA-256 hex of the game ids (lower-case <c>D</c> format, as stored in <c>CHAR(36)</c>) sorted
    /// ordinally and joined with <c>,</c>. The SQL list query computes the same key; a Data test pins the parity.
    /// </summary>
    public static string GamesKey(IEnumerable<Guid> gameIds)
    {
        var joined = string.Join(',', gameIds.Select(id => id.ToString("D")).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
