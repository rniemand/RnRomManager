using RnRomManager.Api.Auth;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/auth/*</c> (spec §23, §9.1, §9.2).</summary>
public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapGet("/antiforgery", (HttpContext ctx) =>
        {
            Session.IssueAntiforgery(ctx);
            return Results.NoContent();
        }).AllowAnonymous();

        auth.MapPost("/login", async (LoginRequest? request, HttpContext ctx, AccountService accounts) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrEmpty(request?.Username))
                errors["username"] = ["Username is required."];
            if (string.IsNullOrEmpty(request?.Password))
                errors["password"] = ["Password is required."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var user = await accounts.LoginAsync(request!.Username!, request.Password!, ctx.RequestAborted);
            if (user is null)
                return Problems.Create(ctx, StatusCodes.Status401Unauthorized, "InvalidCredentials", "Invalid username or password");

            await Session.SignInAsync(ctx, user);
            Session.IssueAntiforgery(ctx);
            return Results.Ok(ToDto(user));
        }).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginRateLimit);

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await Session.SignOutAsync(ctx);
            Session.IssueAntiforgery(ctx);
            return Results.NoContent();
        });

        auth.MapGet("/me", (HttpContext ctx) => Results.Ok(ToDto(Session.CurrentUser(ctx)!)));

        auth.MapPost("/change-password", async (ChangePasswordRequest? request, HttpContext ctx, AccountService accounts) =>
        {
            var (user, errors) = await accounts.ChangePasswordAsync(
                Session.CurrentUser(ctx)!, request?.CurrentPassword, request?.NewPassword, ctx.RequestAborted);
            if (errors is not null)
                return Problems.Validation(ctx, errors);

            // The stamp changed: re-sign this session so only the other sessions die.
            await Session.SignInAsync(ctx, user!);
            Session.IssueAntiforgery(ctx);
            return Results.NoContent();
        });

        return api;
    }

    private static object ToDto(User user) => new
    {
        id = user.Id,
        username = user.Username,
        roles = user.Roles.Order(StringComparer.Ordinal).ToArray(),
        mustChangePassword = user.MustChangePassword,
    };
}
