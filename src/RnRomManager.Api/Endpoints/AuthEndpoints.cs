using System.Text.Json;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/auth/*</c> (spec §23, §9.1, §9.2) and the user's hotkeys (B23).</summary>
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

        // B23: the signed-in user's hotkey bindings (defaults merged in for every editable action).
        auth.MapGet("/me/hotkeys", async (HttpContext ctx, IUserRepository users) =>
        {
            var stored = Hotkeys.ParseStored(await users.GetHotkeysAsync(Session.CurrentUser(ctx)!.Id, ctx.RequestAborted));
            return Results.Ok(new { bindings = Hotkeys.Merge(stored) });
        });

        auth.MapPut("/me/hotkeys", async (JsonElement body, HttpContext ctx, IUserRepository users, ILoggerFactory loggers) =>
        {
            var (requested, errors) = ParseHotkeys(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var user = Session.CurrentUser(ctx)!;
            var stored = Hotkeys.ParseStored(await users.GetHotkeysAsync(user.Id, ctx.RequestAborted));
            var (overrides, ruleErrors) = Hotkeys.Apply(stored, requested!, user.Roles);
            if (overrides is null)
                return Problems.Validation(ctx, ruleErrors);

            await users.SetHotkeysAsync(user.Id, Hotkeys.Serialize(overrides), ctx.RequestAborted);
            loggers.CreateLogger(typeof(AuthEndpoints)).LogInformation("User {UserId} saved {Count} hotkey override(s)", user.Id, overrides.Count);
            return Results.Ok(new { bindings = Hotkeys.Merge(overrides) });
        });

        return api;
    }

    /// <summary><c>{ bindings: { &lt;id&gt;: string | null } }</c> (B23); rule checks happen in <see cref="Hotkeys.Apply"/>.</summary>
    public static (Dictionary<string, string?>? Bindings, Dictionary<string, string[]> Errors) ParseHotkeys(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("bindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Object)
        {
            errors["bindings"] = ["A bindings object is required."];
            return (null, errors);
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in bindings.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString();
            else if (property.Value.ValueKind == JsonValueKind.Null)
                result[property.Name] = null;
            else
                errors[$"bindings.{property.Name}"] = [Hotkeys.FormatError];
        }

        return errors.Count > 0 ? (null, errors) : (result, errors);
    }

    private static object ToDto(User user) => new
    {
        id = user.Id,
        username = user.Username,
        roles = user.Roles.Order(StringComparer.Ordinal).ToArray(),
        mustChangePassword = user.MustChangePassword,
    };
}
