namespace RnRomManager.Api.Endpoints;

public sealed record FileStatus(
    string Name,
    long MinBytes,
    long MaxBytes,
    string Status,
    long? Size,
    string? Md5);
