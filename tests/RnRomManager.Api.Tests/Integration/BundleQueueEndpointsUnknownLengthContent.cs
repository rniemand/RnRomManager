using System.Net.Http.Headers;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>A body without a Content-Length, so the server sees it chunked.</summary>
internal sealed class BundleQueueEndpointsUnknownLengthContent : HttpContent
{
    private readonly HttpContent _inner;

    public BundleQueueEndpointsUnknownLengthContent(HttpContent inner)
    {
        _inner = inner;
        Headers.ContentType = inner.Headers.ContentType ?? new MediaTypeHeaderValue("application/json");
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => _inner.CopyToAsync(stream);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
