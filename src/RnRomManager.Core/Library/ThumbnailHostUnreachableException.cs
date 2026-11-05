namespace RnRomManager.Core.Library;

/// <summary>The thumbnail host stopped answering mid-job (§10.2): the <c>MediaRetry</c> job ends <c>Failed</c>.</summary>
public sealed class ThumbnailHostUnreachableException() : Exception("thumbnail host unreachable");
