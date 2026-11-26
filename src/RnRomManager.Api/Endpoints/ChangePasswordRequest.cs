namespace RnRomManager.Api.Endpoints;

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
