using System.Globalization;
using System.Text.RegularExpressions;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

public static partial class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        var audit = api.MapGroup("/admin/audit").RequireAuthorization(AuthSetup.AdminOnly);
        audit.MapGet("/actions", () => Results.Ok(AuditActions.All));
        audit.MapGet("", async (string? action, string? system, string? user, bool? scheduled, string? q,
            string? from, string? to, int? page, int? pageSize, HttpContext ctx, IAuditRepository repository, ISystemRegistry systems) =>
        {
            var errors = Paging.Validate(page, pageSize);
            user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (action is not null && !AuditActions.All.Contains(action)) errors["action"] = ["Choose an audit action."];
            if (system is not null && systems.ById(system) is null) errors["system"] = ["Choose a registered system."];
            if (scheduled == true && user is not null) errors["user"] = ["Cannot combine a user with scheduled only."];
            if (q?.Length > 200) errors["q"] = ["Must contain at most 200 characters."];
            var start = ParseDate(from, "from", errors);
            var end = ParseDate(to, "to", errors);
            if (start is not null && end is not null && start >= end) errors["to"] = ["Must be later than from."];
            if (errors.Count > 0) return Problems.Validation(ctx, errors);
            var result = await repository.ListAsync(new AuditQuery(action, system, user, scheduled == true, q, start, end),
                page ?? 1, pageSize ?? 100, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, item => new
            {
                item.Id, occurredAt = DateTime.SpecifyKind(item.OccurredAt, DateTimeKind.Utc), item.Action, item.System,
                item.GameId, item.User, item.JobId, item.Path, item.TargetPath, item.Details,
            }));
        });
        return api;
    }

    private static DateTime? ParseDate(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null) return null;
        if (OffsetDate().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return date.UtcDateTime;
        errors[field] = ["Use an ISO 8601 instant with an offset."];
        return null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex OffsetDate();
}
