namespace RnRomManager.Api.Endpoints;

/// <summary>One operator-supplied core file: its exact name, length and lowercase hex SHA-256.</summary>
public sealed record OperatorCoreFile(
    string Name,
    long Length,
    string Sha256,
    string ContentType);
