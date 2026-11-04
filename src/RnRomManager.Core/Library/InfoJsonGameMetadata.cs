namespace RnRomManager.Core.Library;

/// <summary>Game-level <c>metadata</c> (spec §6.2): first / latest write of the ZIP and the app version that wrote it.</summary>
public sealed record InfoJsonGameMetadata(DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string AppVersion);
