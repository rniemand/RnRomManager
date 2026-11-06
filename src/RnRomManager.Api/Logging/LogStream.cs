using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Logging;

/// <summary><see cref="LogStream"/> timing; a singleton so tests can shorten it.</summary>
public sealed class LogStreamOptions
{
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary><c>GET /api/admin/logs/stream</c> (L2): the live log as server-sent events.</summary>
public static class LogStream
{
    public static async Task RunAsync(
        HttpContext ctx, LogBuffer buffer, LogStreamOptions options, IUserRepository users, TimeProvider time,
        IHostApplicationLifetime lifetime)
    {
        // The policy already let an enabled admin in; remember who, to notice when that stops being true.
        var viewer = Session.CurrentUser(ctx)!;
        long? lastId = long.TryParse(ctx.Request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
        var ct = stop.Token;
        using var subscription = buffer.Subscribe(lastId);
        using var timer = new PeriodicTimer(options.KeepAlive, time);
        try
        {
            foreach (var entry in subscription.Backlog)
                await WriteEventAsync(ctx, entry, ct);
            await ctx.Response.Body.FlushAsync(ct);

            var read = subscription.Events.WaitToReadAsync(ct).AsTask();
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            while (true)
            {
                if (await Task.WhenAny(read, tick) == read)
                {
                    if (!await read)
                        return;
                    while (subscription.Events.TryRead(out var entry))
                        await WriteEventAsync(ctx, entry, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    read = subscription.Events.WaitToReadAsync(ct).AsTask();
                }
                else
                {
                    if (!await tick)
                        return;
                    await ctx.Response.WriteAsync(": keep-alive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    var current = await users.GetByIdAsync(viewer.Id, ct);
                    if (current is null || current.IsDisabled || !current.Roles.Contains(Roles.Admin) || current.SecurityStamp != viewer.SecurityStamp)
                        return;
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client gone or app stopping: just end the response.
        }
    }

    private static Task WriteEventAsync(HttpContext ctx, LogEntry entry, CancellationToken ct) =>
        ctx.Response.WriteAsync($"id: {entry.Id.ToString(CultureInfo.InvariantCulture)}\ndata: {JsonSerializer.Serialize(entry, JsonSerializerOptions.Web)}\n\n", ct);
}
