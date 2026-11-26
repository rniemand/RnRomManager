namespace RnRomManager.Core.Ingest;

/// <summary><c>ingest_review_items.reason</c> values added for disc sets (the older ones are literals at their call sites).</summary>
public static class ReviewReasons
{
    /// <summary>B7a: two or more members of a set match the DAT; the items can't be resolved, only deleted (B15).</summary>
    public const string AmbiguousSet = "AmbiguousSet";
}
