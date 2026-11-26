namespace RnRomManager.Core.Ingest;

public enum PlannedKind
{
    /// <summary>A complete cue, TOC, CloneCD or MDS set: processed as one unit.</summary>
    Set,

    /// <summary>A file no cue names: processed on its own as today.</summary>
    Lone,

    /// <summary>Present members of a set that can't be processed; each is <c>Skipped</c> with <see cref="PlannedUnit.Reason"/> and stays.</summary>
    Skipped,
}
