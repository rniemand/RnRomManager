namespace RnRomManager.Core.Library;

/// <summary>How a release was identified (spec §6.2).</summary>
public enum IdentificationMethod
{
    /// <summary>A DAT entry's SHA1 matched, or, for a romset system (<c>DatSource.Mame</c>/<c>Fbneo</c>), a romset whose every required ROM CRC is in its ZIP (Arcade I3).</summary>
    Dat,
    Manual,

    /// <summary>v4: matched by title ID against titledb, which has no hashes.</summary>
    TitleDb,

    /// <summary>v7: a <c>.wbfs</c> matched by its disc's game id against the DAT's serials (its hashes can't match).</summary>
    Serial,
}
