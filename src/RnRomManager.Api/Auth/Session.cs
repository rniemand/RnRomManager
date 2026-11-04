using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Auth;

/// <summary>Signs the session cookie in/out and issues the SPA's <c>XSRF-TOKEN</c> cookie (spec §9.2).</summary>
public static class Session
{
    public const string StampClaim = "rnrm:stamp";
    public const string XsrfCookie = "XSRF-TOKEN";

    /// <summary>The DB row loaded while validating this request's cookie; null when anonymous.</summary>
    public static User? CurrentUser(HttpContext ctx) => ctx.Items[typeof(User)] as User;

    public static async Task SignInAsync(HttpContext ctx, User user)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(StampClaim, user.SecurityStamp),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = true });
        // Antiforgery tokens are bound to the user, so the re-issued one must see the new principal.
        ctx.User = principal;
        ctx.Items[typeof(User)] = user;
    }

    public static async Task SignOutAsync(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        ctx.Items.Remove(typeof(User));
    }

    /// <summary>Sets the antiforgery cookie token plus the JS-readable <c>XSRF-TOKEN</c> request token for the current user.</summary>
    public static void IssueAntiforgery(HttpContext ctx)
    {
        var tokens = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx);
        ctx.Response.Cookies.Append(XsrfCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            Path = "/",
        });
    }
}
