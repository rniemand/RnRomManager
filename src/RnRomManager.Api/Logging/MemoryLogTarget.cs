using NLog;
using NLog.Targets;

namespace RnRomManager.Api.Logging;

/// <summary>
/// NLog target <c>LiveLog</c> (nlog.config): renders each event into the <see cref="LogBuffer"/> that
/// <see cref="NLogSetup"/> attaches; drops events until then.
/// </summary>
[Target("LiveLog")]
public sealed class MemoryLogTarget : Target
{
    public LogBuffer? Buffer { get; set; }

    protected override void Write(LogEventInfo logEvent) =>
        Buffer?.Add(
            logEvent.TimeStamp.ToUniversalTime(),
            logEvent.Level.Name,
            logEvent.LoggerName ?? "",
            logEvent.FormattedMessage,
            logEvent.Exception?.ToString());
}
