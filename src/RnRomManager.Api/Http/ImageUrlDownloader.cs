using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;

namespace RnRomManager.Api.Http;

/// <summary>U3/U4: which addresses an image-URL download may reach, and how long it may take (tests shorten both).</summary>
public sealed record ImageUrlPolicy(Func<IPAddress, bool> IsAllowed, TimeSpan Timeout)
{
    public static ImageUrlPolicy Default { get; } = new(PublicAddress.IsPublic, TimeSpan.FromSeconds(15));
}

/// <summary>Thrown by the connect callback when a host resolves to an address the policy refuses (U3).</summary>
public sealed class BlockedAddressException(string host) : Exception($"{host} resolves to a non-public address.");

/// <summary>
/// The outcome of <see cref="ImageUrlDownloader.DownloadAsync"/>: the body (at most <see cref="ImageFormats.MaxBytes"/> + 1
/// bytes, so an oversized image is detectable without reading it all), or a problem to return as is.
/// </summary>
public sealed record ImageUrlDownload(byte[]? Bytes, int Status = StatusCodes.Status200OK, string? Code = null, string? Title = null);

/// <summary>
/// U4: one GET with no retries, one timeout over the headers and the whole body, and a bounded read whatever
/// <c>Content-Length</c> says. The content type is ignored; the caller checks the bytes.
/// </summary>
public sealed class ImageUrlDownloader(HttpClient client, ImageUrlPolicy policy)
{
    public const string Failed = "ImageDownloadFailed";
    public const string Blocked = "ImageUrlBlocked";

    public async Task<ImageUrlDownload> DownloadAsync(Uri url, CancellationToken aborted)
    {
        using var timeout = new CancellationTokenSource(policy.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, aborted);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var status = (int)response.StatusCode;
            // The handler follows up to 3 redirects; a 3xx left over is one too many (or an https→http hop).
            if (status is >= 300 and < 400)
                return Fail("too many redirects.");
            if (!response.IsSuccessStatusCode)
                return Fail($"HTTP {status}.");

            await using var body = await response.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (buffer.Length <= ImageFormats.MaxBytes)
            {
                var room = (int)Math.Min(chunk.Length, ImageFormats.MaxBytes + 1 - buffer.Length);
                var read = await body.ReadAsync(chunk.AsMemory(0, room), linked.Token);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
            }
            return new(buffer.ToArray());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            return Fail("timed out.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException)
        {
            return new(null, StatusCodes.Status400BadRequest, Blocked, "The URL points to a private or local network address.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Fail("couldn't connect.");
        }
    }

    private static ImageUrlDownload Fail(string reason) =>
        new(null, StatusCodes.Status502BadGateway, Failed, $"Couldn't download the image: {reason}");

    /// <summary>
    /// U3: no proxy, at most 3 redirects, and every connection (each redirect hop included) goes to an address the
    /// policy allows. All resolved addresses must pass, and the socket connects to those vetted addresses only, so a
    /// DNS answer can't change between the check and the connect.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Func<IPAddress, bool> isAllowed) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host.Trim('[', ']');
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0 || !addresses.All(isAllowed))
                throw new BlockedAddressException(host);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
