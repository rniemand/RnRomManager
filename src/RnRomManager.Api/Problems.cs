using System.Diagnostics;

namespace RnRomManager.Api;

/// <summary>Builds RFC 7807 ProblemDetails with the extra <c>code</c> and <c>traceId</c> (spec §23).</summary>
public static class Problems
{
    /// <param name="extensions">Extra members (e.g. RP8's <c>merged</c>), added next to <c>code</c> and <c>traceId</c>.</param>
    public static IResult Create(HttpContext ctx, int status, string code, string title,
        IReadOnlyDictionary<string, object?>? extensions = null) =>
        Results.Problem(statusCode: status, title: title, extensions: Extensions(ctx, code, extensions));

    public static IResult Validation(HttpContext ctx, IDictionary<string, string[]> errors,
        IReadOnlyDictionary<string, object?>? extensions = null) =>
        Results.ValidationProblem(errors, extensions: Extensions(ctx, "ValidationFailed", extensions));

    private static Dictionary<string, object?> Extensions(HttpContext ctx, string code, IReadOnlyDictionary<string, object?>? extra)
    {
        var all = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["traceId"] = Activity.Current?.Id ?? ctx.TraceIdentifier,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, object?>())
            all[key] = value;
        return all;
    }

    public static IResult Unauthorized(HttpContext ctx) =>
        Create(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Not signed in");

    public static IResult Forbidden(HttpContext ctx) =>
        Create(ctx, StatusCodes.Status403Forbidden, "Forbidden", "Not allowed");

    public static IResult NotFound(HttpContext ctx, IReadOnlyDictionary<string, object?>? extensions = null) =>
        Create(ctx, StatusCodes.Status404NotFound, "NotFound", "Not found", extensions);
}
