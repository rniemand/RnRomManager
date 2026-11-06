using System.Threading.Channels;

namespace RnRomManager.Api.Logging;

/// <summary>One rendered log event for the live view (L1). Times are UTC.</summary>
public sealed record LogEntry(long Id, DateTime Time, string Level, string Logger, string Message, string? Exception);

/// <summary>
/// The last <see cref="Capacity"/> log events in memory, fed by <see cref="MemoryLogTarget"/> (L1). Ids start at the
/// creation time in Unix ms × 1000 and increase by one, so they keep increasing across restarts (an SSE client's
/// <c>Last-Event-ID</c> from before a restart is lower than every new id).
/// </summary>
public sealed class LogBuffer(TimeProvider time, int capacity = LogBuffer.DefaultCapacity)
{
    public const int DefaultCapacity = 500;

    // A subscriber that stops reading loses its oldest events instead of growing memory.
    private const int SubscriberQueue = 1000;

    private readonly Lock _lock = new();
    private readonly Queue<LogEntry> _events = new(capacity);
    private readonly List<Channel<LogEntry>> _subscribers = [];
    private long _nextId = time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;

    public int Capacity => capacity;

    public LogEntry Add(DateTime timeUtc, string level, string logger, string message, string? exception)
    {
        lock (_lock)
        {
            var entry = new LogEntry(_nextId++, timeUtc, level, logger, message, exception);
            if (_events.Count == capacity)
                _events.Dequeue();
            _events.Enqueue(entry);
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(entry);
            return entry;
        }
    }

    /// <summary>
    /// Buffered events with an id above <paramref name="lastId"/>, oldest first. Every buffered event when
    /// <paramref name="lastId"/> is null or above the newest id (a client from another process or buffer, L2).
    /// </summary>
    public IReadOnlyList<LogEntry> Since(long? lastId)
    {
        lock (_lock)
        {
            if (lastId is null || _events.Count == 0 || lastId > _events.Last().Id)
                return [.. _events];
            return [.. _events.Where(e => e.Id > lastId)];
        }
    }

    /// <summary>
    /// The buffered events since <paramref name="lastId"/> (as <see cref="Since"/>) plus a reader of every later event,
    /// taken under one lock so nothing is missed or sent twice. Dispose to stop receiving.
    /// </summary>
    public Subscription Subscribe(long? lastId)
    {
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(SubscriberQueue)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock)
        {
            _subscribers.Add(channel);
            return new Subscription(this, channel, Since(lastId));
        }
    }

    private void Unsubscribe(Channel<LogEntry> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(LogBuffer owner, Channel<LogEntry> channel, IReadOnlyList<LogEntry> backlog) : IDisposable
    {
        public IReadOnlyList<LogEntry> Backlog => backlog;

        public ChannelReader<LogEntry> Events => channel.Reader;

        public void Dispose() => owner.Unsubscribe(channel);
    }
}
