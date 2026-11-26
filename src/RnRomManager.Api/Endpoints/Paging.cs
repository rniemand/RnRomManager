using RnRomManager.Core.Database;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>?page=1&amp;pageSize=48</c> (max 200) → <c>{ items, page, pageSize, total }</c> (spec §23).</summary>
public static class Paging
{
    public static Dictionary<string, string[]> Validate(int? page, int? pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
            errors["page"] = ["Must be 1 or more."];
        if (pageSize is < 1 or > Page<object>.MaxSize)
            errors["pageSize"] = [$"Must be 1–{Page<object>.MaxSize}."];
        return errors;
    }

    public static object ToDto<T>(Page<T> page, Func<T, object> item) => new
    {
        items = page.Items.Select(item),
        page = page.PageNumber,
        pageSize = page.PageSize,
        total = page.Total,
    };
}
