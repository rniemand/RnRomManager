namespace RnRomManager.Api.Logging;

/// <summary><see cref="LogStream"/> timing; a singleton so tests can shorten it.</summary>
public sealed class LogStreamOptions
{
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);
}
