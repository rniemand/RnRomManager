namespace RnRomManager.Core.Storage;

public sealed class StorageNotWritableException(string message, Exception inner) : Exception(message, inner);
