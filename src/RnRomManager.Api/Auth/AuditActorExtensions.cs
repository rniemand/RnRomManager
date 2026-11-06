using System.Security.Claims;
using RnRomManager.Core.Audit;

namespace RnRomManager.Api.Auth;

public static class AuditActorExtensions
{
    extension(AuditActor)
    {
        /// <summary>AU3: the request's actor, from the session's claims; no job.</summary>
        public static AuditActor From(ClaimsPrincipal user) =>
            new(int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
                user.FindFirstValue(ClaimTypes.Name), null);
    }
}
