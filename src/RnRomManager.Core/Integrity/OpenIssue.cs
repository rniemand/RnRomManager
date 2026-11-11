namespace RnRomManager.Core.Integrity;

public sealed record OpenIssue(long Id, string Type);

/// <summary>
/// An open issue that may concern one external ROM file (spec I6), with what it was stored under. The lookup matches
/// with the table's accent-insensitive collation, so callers compare <see cref="ZipPath"/>, <see cref="EntryName"/>
/// and <see cref="GameId"/> themselves before acting on it.
/// </summary>
public sealed record ExternalOpenIssue(long Id, string Type, string? ZipPath, string? EntryName, Guid? GameId);
