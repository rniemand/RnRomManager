namespace RnRomManager.Core.Library;

/// <summary>Why a ZIP's DB rows were left as they are (§12.2).</summary>
public enum DbSyncSkip
{
    ZipUnreadable,
    InfoJsonMissing,
    InfoJsonInvalid,
    InfoJsonNewerSchema,
    DuplicateGameId,
    DuplicateSha1,
}
