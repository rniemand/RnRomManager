namespace RnRomManager.Api.Logging;

/// <summary>One rendered log event for the live view (L1). Times are UTC.</summary>
public sealed record LogEntry(
    long Id,
    DateTime Time,
    string Level,
    string Logger,
    string Message,
    string? Exception);
