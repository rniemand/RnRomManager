namespace RnRomManager.Api.Tests;

internal sealed class SlowContent(Task gate, byte[] body, CancellationToken request) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        await gate;
        request.ThrowIfCancellationRequested();
        await stream.WriteAsync(body);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
