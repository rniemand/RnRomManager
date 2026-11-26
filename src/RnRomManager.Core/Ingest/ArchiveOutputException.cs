namespace RnRomManager.Core.Ingest;

/// <summary>
/// An extracted copy couldn't be created, written or closed in the work dir: an <c>IoError</c> of this app's own
/// storage, not a fault of the archive.
/// </summary>
public sealed class ArchiveOutputException(string message, Exception inner) : Exception(message, inner);
