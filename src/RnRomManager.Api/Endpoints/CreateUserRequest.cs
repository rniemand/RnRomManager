namespace RnRomManager.Api.Endpoints;

public sealed record CreateUserRequest(string? Username, IReadOnlyList<string?>? Roles, string? Password);
