using System.Net;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>The inner content without a length, so it's sent chunked.</summary>
internal sealed class DiscoveryLogoEndpointsUnknownLengthContent : HttpContent
{
    private readonly HttpContent _inner;

    public DiscoveryLogoEndpointsUnknownLengthContent(HttpContent inner)
    {
        _inner = inner;
        foreach (var header in inner.Headers.Where(h => h.Key != "Content-Length"))
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

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
