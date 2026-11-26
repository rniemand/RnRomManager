using System.Security.Claims;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;

namespace RnRomManager.Api.Endpoints;

/// <summary>The signed-in caller's user id.</summary>
public static class EndpointUser
{
    public static int? Id(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>For endpoints behind an authorization policy, where the id claim is always present.</summary>
    internal static int RequiredId(HttpContext ctx) =>
        AuditActor.From(ctx.User).UserId ?? throw new InvalidOperationException("No user id claim.");
}
