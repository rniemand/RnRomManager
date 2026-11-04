namespace RnRomManager.Core.Integrity;

/// <summary><c>integrity_issues.type</c> values used so far (spec §12.2).</summary>
public static class IntegrityIssueType
{
    public const string HashMismatch = "HashMismatch";
    public const string EntryMissing = "EntryMissing";
    public const string EntrySizeMismatch = "EntrySizeMismatch";

    /// <summary>The issues ingesting a byte-identical copy can fix (§7.2 step 3, D30).</summary>
    public static IReadOnlyList<string> SelfRepairable { get; } = [HashMismatch, EntryMissing, EntrySizeMismatch];
}

public sealed record OpenIssue(long Id, string Type);

/// <summary><c>integrity_issues</c> (spec §14.1). Grows with the integrity checker (S36).</summary>
public interface IIntegrityIssueRepository
{
    /// <summary>Open issues of these types on one ZIP entry.</summary>
    Task<IReadOnlyList<OpenIssue>> ListOpenAsync(string zipPath, string entryName, IReadOnlyCollection<string> types, CancellationToken cancellationToken);

    Task ResolveAsync(IReadOnlyCollection<long> ids, DateTime resolvedAt, CancellationToken cancellationToken);
}
