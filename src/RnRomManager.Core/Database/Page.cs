namespace RnRomManager.Core.Database;

/// <summary>One page of a list (spec §23: <c>{ items, page, pageSize, total }</c>). <c>page</c> is 1-based.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, long Total)
{
    public const int DefaultSize = 48;
    public const int MaxSize = 200;

    public static int Offset(int page, int pageSize) => (page - 1) * pageSize;
}
