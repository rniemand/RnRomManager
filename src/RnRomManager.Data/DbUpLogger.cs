using System.Globalization;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace RnRomManager.Data;

/// <summary>Routes DbUp output through the app's logging (NLog). DbUp's chatty Information lines go to Debug.</summary>
internal sealed class DbUpLogger(ILogger logger) : IUpgradeLog
{
    public void LogTrace(string format, params object[] args) => Write(LogLevel.Trace, null, format, args);

    public void LogDebug(string format, params object[] args) => Write(LogLevel.Debug, null, format, args);

    public void LogInformation(string format, params object[] args) => Write(LogLevel.Debug, null, format, args);

    public void LogWarning(string format, params object[] args) => Write(LogLevel.Warning, null, format, args);

    public void LogError(string format, params object[] args) => Write(LogLevel.Error, null, format, args);

    public void LogError(Exception ex, string format, params object[] args) => Write(LogLevel.Error, ex, format, args);

    private void Write(LogLevel level, Exception? ex, string format, object[] args) =>
        logger.Log(level, ex, "DbUp: {Message}", args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args));
}
