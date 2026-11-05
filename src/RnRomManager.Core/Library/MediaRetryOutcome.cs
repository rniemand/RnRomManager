namespace RnRomManager.Core.Library;

public enum MediaRetryOutcome
{
    /// <summary>Something new was found; the ZIP and DB were rewritten.</summary>
    Updated,

    /// <summary>The lookup found nothing new; nothing was written.</summary>
    NotFound,

    /// <summary>Not looked up: the game needs no media, or (job only) every release is <c>Manual</c>.</summary>
    Skipped,

    /// <summary>The ZIP couldn't be read or written (<see cref="MediaRetryResult.Code"/>).</summary>
    Error,
}
