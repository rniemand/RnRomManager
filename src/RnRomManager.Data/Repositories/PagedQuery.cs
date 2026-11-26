using System.Data.Common;
using Dapper;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary>Count-then-page on the caller's connection; both queries get the caller's args (which hold <c>pageSize</c> and <c>offset</c>).</summary>
public static class PagedQuery
{
    public static async Task<Page<T>> QueryPageAsync<T>(
        this DbConnection connection,
        string countSql,
        string pageSql,
        object args,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, args, cancellationToken: ct));
        var rows = await connection.QueryAsync<T>(new CommandDefinition(pageSql, args, cancellationToken: ct));
        return new Page<T>(rows.AsList(), page, pageSize, total);
    }
}
