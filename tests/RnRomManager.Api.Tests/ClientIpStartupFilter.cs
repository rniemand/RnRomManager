using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace RnRomManager.Api.Tests;

internal sealed class ClientIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(ApiFactory.ClientHeader, out var client) && int.TryParse(client, out var id))
                context.Connection.RemoteIpAddress = IPAddress.Parse($"10.{(id >> 16) & 255}.{(id >> 8) & 255}.{id & 255}");
            return nextMiddleware(context);
        });
        next(app);
    };
}
