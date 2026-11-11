namespace RnRomManager.Core.Library;

/// <summary>How a release was identified (spec §6.2).</summary>
public enum IdentificationMethod
{
    Dat,
    Manual,

    /// <summary>v4: matched by title ID against titledb, which has no hashes.</summary>
    TitleDb,
}
