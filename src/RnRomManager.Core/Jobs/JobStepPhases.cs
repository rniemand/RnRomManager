namespace RnRomManager.Core.Jobs;

/// <summary>The streamed phases a job reports per file (spec B2).</summary>
public static class JobStepPhases
{
    public const string Hashing = "Hashing";
    public const string CopyingToLibrary = "CopyingToLibrary";
    public const string Copying = "Copying";
}
