using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Auth;

/// <summary>Cookie session, policies, antiforgery, login rate limit and the must-change-password gate (spec §9.1, §9.2).</summary>
public static class AuthSetup
{
    public const string Authenticated = "Authenticated";
    public const string AdminOnly = "AdminOnly";
    public const string LoginRateLimit = "login";
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    // spec §9.1 (D42): what a user who must change their password can still call.
    private static readonly string[] MustChangeAllowed =
        ["/api/auth/me", "/api/auth/change-password", "/api/auth/logout", "/api/auth/antiforgery", "/api/auth/login"];

    public static IServiceCollection AddAppAuth(this IServiceCollection services)
    {
        services.AddSingleton<AccountService>();
        services.AddSingleton<UserAdminService>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "rnrm.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
                options.Events.OnRedirectToLogin = ctx => WriteAsync(ctx.HttpContext, Problems.Unauthorized(ctx.HttpContext));
                options.Events.OnRedirectToAccessDenied = ctx => WriteAsync(ctx.HttpContext, Problems.Forbidden(ctx.HttpContext));
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(AdminOnly, policy => policy.RequireRole(nameof(UserRole.Admin)));
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeader;
            options.Cookie.Name = "rnrm.af";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddRateLimiter(options =>
        {
            // RemoteIpAddress is the forwarded client only when ASPNETCORE_FORWARDEDHEADERS_ENABLED is set (spec §4).
            options.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
            options.OnRejected = (ctx, _) => new ValueTask(
                Problems.Create(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "TooManyRequests", "Too many login attempts").ExecuteAsync(ctx.HttpContext));
        });
        return services;
    }

    public static WebApplication UseAppAuth(this WebApplication app)
    {
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(async (ctx, next) =>
        {
            if (Session.CurrentUser(ctx) is { MustChangePassword: true }
                && ctx.Request.Path.StartsWithSegments("/api")
                && !MustChangeAllowed.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                await Problems.Create(ctx, StatusCodes.Status403Forbidden, "PasswordChangeRequired", "Password change required").ExecuteAsync(ctx);
                return;
            }
            await next(ctx);
        });
        app.UseAuthorization();
        return app;
    }

    /// <summary>Every mutating call in the group needs a valid <c>X-XSRF-TOKEN</c> header (spec §9.2).</summary>
    public static RouteGroupBuilder RequireAntiforgery(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method)
                || HttpMethods.IsPatch(http.Request.Method) || HttpMethods.IsDelete(http.Request.Method))
            {
                if (!await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
                    return Problems.Create(http, StatusCodes.Status400BadRequest, "AntiforgeryInvalid", "Missing or invalid antiforgery token");
            }
            return await next(ctx);
        });

    /// <summary>Every request: the user must still exist, be enabled and have the stamp the cookie was issued with (spec §9.2).</summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal!;
        var user = int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await ctx.HttpContext.RequestServices.GetRequiredService<IUserRepository>().GetByIdAsync(id, ctx.HttpContext.RequestAborted)
            : null;
        if (user is null || user.IsDisabled || user.SecurityStamp != principal.FindFirstValue(Session.StampClaim))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        ctx.HttpContext.Items[typeof(User)] = user;
    }

    private static Task WriteAsync(HttpContext ctx, IResult result) => result.ExecuteAsync(ctx);
}
