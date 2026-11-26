namespace RnRomManager.Api.Endpoints;

public sealed record UpdateUserRequest(IReadOnlyList<string?>? Roles, bool? IsDisabled);
