namespace RnRomManager.Core.Integrity;

/// <summary><c>integrity_issues.type</c> values and their severities (spec §12.2).</summary>
public static class IntegrityIssueType
{
    public const string ZipUnreadable = "ZipUnreadable";
    public const string InfoJsonMissing = "InfoJsonMissing";
    public const string InfoJsonInvalid = "InfoJsonInvalid";
    public const string InfoJsonNewerSchema = "InfoJsonNewerSchema";
    public const string EntryMissing = "EntryMissing";
    public const string EntrySizeMismatch = "EntrySizeMismatch";
    public const string EntryUnlisted = "EntryUnlisted";
    public const string HashMismatch = "HashMismatch";
    public const string MissingBoxArt = "MissingBoxArt";
    public const string DefaultBoxArt = "DefaultBoxArt";
    public const string WrongLocation = "WrongLocation";
    public const string OrphanFile = "OrphanFile";
    public const string DuplicateGameId = "DuplicateGameId";
    public const string DuplicateSha1 = "DuplicateSha1";
    public const string DbRowInserted = "DbRowInserted";
    public const string DbRowUpdated = "DbRowUpdated";
    public const string DbRowDeleted = "DbRowDeleted";

    /// <summary>The issues ingesting a byte-identical copy can fix (§7.2 step 3, D30).</summary>
    public static IReadOnlyList<string> SelfRepairable { get; } = [HashMismatch, EntryMissing, EntrySizeMismatch];

    /// <summary>The DB auto-repairs: recorded as <c>AutoRepaired</c>, never <c>Open</c>.</summary>
    public static IReadOnlyList<string> Repairs { get; } = [DbRowInserted, DbRowUpdated, DbRowDeleted];

    /// <returns><c>Info</c>, <c>Warn</c> or <c>Error</c> (§12.2 table).</returns>
    public static string SeverityOf(string type) => type switch
    {
        InfoJsonNewerSchema or EntryUnlisted or WrongLocation or OrphanFile or DuplicateSha1 => IssueSeverity.Warn,
        DefaultBoxArt or DbRowInserted or DbRowUpdated or DbRowDeleted => IssueSeverity.Info,
        _ => IssueSeverity.Error,
    };
}
