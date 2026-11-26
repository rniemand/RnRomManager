namespace RnRomManager.Core.Ingest;

internal sealed class SourceReadException(Exception inner) : Exception(inner.Message, inner);
