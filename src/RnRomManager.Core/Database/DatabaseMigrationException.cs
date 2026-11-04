namespace RnRomManager.Core.Database;

public sealed class DatabaseMigrationException(string message, Exception? inner) : Exception(message, inner);
