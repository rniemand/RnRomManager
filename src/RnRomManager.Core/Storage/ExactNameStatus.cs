namespace RnRomManager.Core.Storage;

/// <summary>B10: the outcome of <see cref="ExternalRomName.AllocateExact"/>.</summary>
public enum ExactNameStatus
{
    Allocated,
    /// <summary>The name or its staging name is already used in the folder (on disk, by a claim or by the destination ZIP).</summary>
    Taken,
    /// <summary>Over <see cref="ExternalRomName.MaxNameBytes"/> UTF-8 bytes; it is never cut.</summary>
    TooLong,
}
