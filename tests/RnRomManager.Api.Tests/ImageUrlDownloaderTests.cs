using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests;

/// <summary>U3/U4 error mapping on <see cref="ImageUrlDownloader"/> itself, against a real loopback server.</summary>
public class ImageUrlDownloaderTests
{
    private StubImageServer _stub = null!;

    [SetUp]
    public async Task SetUp() => _stub = await StubImageServer.StartAsync();

    [TearDown]
    public async Task TearDown() => await _stub.DisposeAsync();

    private static ImageUrlDownloader Create(Func<IPAddress, bool> isAllowed, TimeSpan? timeout = null) =>
        new(new HttpClient(ImageUrlDownloader.CreateHandler(isAllowed)) { Timeout = Timeout.InfiniteTimeSpan },
            new ImageUrlPolicy(isAllowed, timeout ?? TimeSpan.FromSeconds(5)));

    [Test]
    public void ACancelledCaller_IsRethrown_NotATimeout()
    {
        _stub.Routes["/slow.png"] = async ctx => await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted).ContinueWith(_ => { });
        using var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.That(async () => await Create(_ => true).DownloadAsync(_stub.Url("/slow.png"), aborted.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ABlockedAddress_IsImageUrlBlocked_WithoutConnecting()
    {
        var download = await Create(_ => false).DownloadAsync(_stub.Url("/x.png"), default);

        Assert.That((download.Bytes, download.Status, download.Code, download.Title),
            Is.EqualTo(((byte[]?)null, 400, ImageUrlDownloader.Blocked, "The URL points to a private or local network address.")));
        Assert.That(_stub.Requests, Is.Empty);
    }

    [Test]
    public async Task OneBlockedAddressAmongSeveral_Blocks()
    {
        // Refusing one of localhost's addresses (127.0.0.1 and ::1) refuses the host.
        Assume.That(Dns.GetHostAddresses("localhost"), Has.Member(IPAddress.IPv6Loopback).And.Member(IPAddress.Loopback));
        var download = await Create(a => !a.Equals(IPAddress.IPv6Loopback)).DownloadAsync(new Uri($"http://localhost:{_stub.BaseUrl.Port}/x.png"), default);

        Assert.That(download.Code, Is.EqualTo(ImageUrlDownloader.Blocked));
    }

    [Test]
    public async Task TheBodyRead_StopsOneByteOverTheLimit()
    {
        var huge = new byte[ImageFormats.MaxBytes + 4096];
        _stub.Routes["/huge"] = StubImageServer.Bytes(huge);

        var download = await Create(_ => true).DownloadAsync(_stub.Url("/huge"), default);

        Assert.That(download.Bytes!.Length, Is.EqualTo(ImageFormats.MaxBytes + 1));
    }

    [Test]
    public void TheRegisteredClient_HasAnInfiniteClientTimeout_AndTheUserAgent()
    {
        using var provider = new ServiceCollection().AddLogging().AddExternalHttpClients().BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.ImageUrls);

        Assert.That(client.Timeout, Is.EqualTo(Timeout.InfiniteTimeSpan));
        Assert.That(client.DefaultRequestHeaders.UserAgent.ToString(), Does.StartWith("RnRomManager/"));
    }
}
