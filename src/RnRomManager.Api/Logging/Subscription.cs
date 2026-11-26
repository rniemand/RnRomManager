using System.Threading.Channels;

namespace RnRomManager.Api.Logging;

public sealed class Subscription(LogBuffer owner, Channel<LogEntry> channel, IReadOnlyList<LogEntry> backlog) : IDisposable
{
    public IReadOnlyList<LogEntry> Backlog => backlog;

    public ChannelReader<LogEntry> Events => channel.Reader;

    public void Dispose() => owner.Unsubscribe(channel);
}
