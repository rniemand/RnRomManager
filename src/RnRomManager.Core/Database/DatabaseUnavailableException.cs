namespace RnRomManager.Core.Database;

public sealed class DatabaseUnavailableException(string message, Exception inner) : Exception(message, inner);
