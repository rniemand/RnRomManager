namespace RnRomManager.Core.Reports;

/// <summary>D1: how duplicate games are grouped: by release base title (RP6), a shared IGDB id, or (D2) a shared file hash.</summary>
public enum DuplicateMatch
{
    Title,
    Igdb,
    Hash,
}
