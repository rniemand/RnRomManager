namespace RnRomManager.Core.Ingest;

/// <summary>An item hit <c>DbWriteFailed</c>: the job ends <c>Failed</c> with this message (§7.2 step 10).</summary>
public sealed class IngestStoppedException(string message) : Exception(message);
