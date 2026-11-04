using System.Diagnostics;

namespace RnRomManager.Api;

/// <summary>Builds RFC 7807 ProblemDetails with the extra <c>code</c> and <c>traceId</c> (spec §23).</summary>
public static class Problems
{
    public static IResult Create(HttpContext ctx, int status, string code, string title) =>
        Results.Problem(
            statusCode: status,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = Activity.Current?.Id ?? ctx.TraceIdentifier,
            });

    public static IResult Validation(HttpContext ctx, IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "ValidationFailed",
                ["traceId"] = Activity.Current?.Id ?? ctx.TraceIdentifier,
            });

    public static IResult Unauthorized(HttpContext ctx) =>
        Create(ctx, StatusCodes.Status401Unauthorized, "Unauthorized", "Not signed in");

    public static IResult Forbidden(HttpContext ctx) =>
        Create(ctx, StatusCodes.Status403Forbidden, "Forbidden", "Not allowed");

    public static IResult NotFound(HttpContext ctx) =>
        Create(ctx, StatusCodes.Status404NotFound, "NotFound", "Not found");
}
