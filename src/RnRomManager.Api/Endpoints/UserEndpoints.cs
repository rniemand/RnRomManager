using RnRomManager.Api.Auth;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/users*</c>, Admin only (spec §23, §9.1).</summary>
public static class UserEndpoints
{
    public sealed record CreateUserRequest(string? Username, string? Role, string? Password);

    public sealed record UpdateUserRequest(string? Role, bool? IsDisabled);

    public sealed record ResetPasswordRequest(string? NewPassword);

    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(AuthSetup.AdminOnly);

        users.MapGet("", async (IUserRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ListAsync(ct)).Select(ToDto)));

        users.MapPost("", async (CreateUserRequest? request, HttpContext ctx, UserAdminService admin) =>
            ToResult(ctx, await admin.CreateAsync(request?.Username, request?.Role, request?.Password, ctx.RequestAborted),
                user => Results.Created($"/api/users/{user.Id}", ToDto(user))));

        users.MapPatch("/{id:int}", async (int id, UpdateUserRequest? request, HttpContext ctx, UserAdminService admin) =>
            ToResult(ctx, await admin.UpdateAsync(id, request?.Role, request?.IsDisabled, ctx.RequestAborted),
                user => Results.Ok(ToDto(user))));

        users.MapPost("/{id:int}/reset-password", async (int id, ResetPasswordRequest? request, HttpContext ctx, UserAdminService admin) =>
            ToResult(ctx, await admin.ResetPasswordAsync(id, request?.NewPassword, ctx.RequestAborted),
                _ => Results.NoContent()));

        return api;
    }

    private static IResult ToResult(HttpContext ctx, UserAdminResult result, Func<User, IResult> ok) => result.Error switch
    {
        UserAdminResult.Failure.None => ok(result.User!),
        UserAdminResult.Failure.NotFound => Problems.NotFound(ctx),
        UserAdminResult.Failure.ValidationFailed => Problems.Validation(ctx, result.Errors!),
        UserAdminResult.Failure.UsernameTaken => Problems.Create(ctx, StatusCodes.Status409Conflict, "UsernameTaken", "Username is already taken"),
        UserAdminResult.Failure.LastAdmin => Problems.Create(ctx, StatusCodes.Status409Conflict, "LastAdmin", "The last enabled Admin can't be disabled or demoted"),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static object ToDto(User user) => new
    {
        id = user.Id,
        username = user.Username,
        role = user.Role.ToString(),
        isDisabled = user.IsDisabled,
        lastLoginAt = user.LastLoginAt,
    };
}
