using System.Net;

namespace RnRomManager.Api.Tests;

/// <summary>Headers at once; the body waits for <paramref name="gate"/> and fails if the request was cancelled meanwhile.</summary>
internal sealed class SlowBodyHandler(Task gate, byte[] body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent(gate, body, cancellationToken) });
}
