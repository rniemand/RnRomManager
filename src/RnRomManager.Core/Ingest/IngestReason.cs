namespace RnRomManager.Core.Ingest;

/// <summary><c>job_items.reason</c> codes (spec §23.1) used so far.</summary>
public static class IngestReason
{
    public const string TooRecent = "TooRecent";

    /// <summary>B9: a <c>.zip</c>/<c>.7z</c> for a <c>FileSets</c> system stays in ingest; nothing is extracted.</summary>
    public const string ArchiveNotSupported = "ArchiveNotSupported";

    /// <summary>B7, CloneCD B5, D8: a member of a cue, TOC, CloneCD or MDS set waits in ingest: a file its cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> names is missing, that sheet is broken, an <c>.img</c>/<c>.sub</c> has no <c>.ccd</c>, an <c>.mdf</c> has no <c>.mds</c>, or a lone data track of a multi-track disc.</summary>
    public const string IncompleteSet = "IncompleteSet";

    /// <summary>B7, CloneCD B5, D8: a cue or <c>.toc</c> names a file that isn't a <c>.bin</c> or names itself, or a member of a cue, TOC, CloneCD or MDS set has a name that can't be stored.</summary>
    public const string UnsupportedSetMember = "UnsupportedSetMember";

    /// <summary>B7, CloneCD B5, D8: a file is named by two cues or <c>.toc</c>s, a cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> member name matches two files that differ only by case, or a CloneCD or MDS member is already another set's.</summary>
    public const string SetConflict = "SetConflict";

    /// <summary>B7a: two or more games own the data track's base title; the set stays until the Admin merges or moves them.</summary>
    public const string AmbiguousGroup = "AmbiguousGroup";

    /// <summary>
    /// B7a: an unmatched set member whose SHA1 another source's Pending review row already holds (silent audio tracks
    /// repeat); it stays, since moving it to <c>_duplicates/</c> would break its set.
    /// </summary>
    public const string AlreadyInReview = "AlreadyInReview";
}
