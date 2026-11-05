namespace RnRomManager.Core.Library;

/// <param name="Code">For <see cref="MediaRetryOutcome.Error"/>: a §23.1 code (<c>TargetZipUnreadable</c>, <c>VerifyFailed</c>, …).</param>
public sealed record MediaRetryResult(MediaRetryOutcome Outcome, string? Code = null, string? Details = null);
