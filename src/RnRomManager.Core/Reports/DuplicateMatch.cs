namespace RnRomManager.Core.Reports;

/// <summary>D1: how duplicate games are grouped: by release base title (RP6), or by a shared IGDB id.</summary>
public enum DuplicateMatch
{
    Title,
    Igdb,
}
