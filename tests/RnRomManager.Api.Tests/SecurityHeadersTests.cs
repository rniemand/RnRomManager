using System.Net;
using Microsoft.AspNetCore.Hosting;
using RnRomManager.Api.Security;

namespace RnRomManager.Api.Tests;

/// <summary>Spec §9.2 response headers and the CSP script hashes.</summary>
public class SecurityHeadersTests
{
    private const string IndexHtml = """
        <!doctype html>
        <html><head>
        <script src="/theme.js"></script>
        <script>alert(1)</script>
        </head><body><div style="display: contents"><SCRIPT type="module">
          console.log("two");
        </SCRIPT></div></body></html>
        """;

    private const string HashOne = "'sha256-bhHHL3z2vDgxUt0W3dWQOrprscmda2Y5pLsLg4GF+pI='";
    private const string HashTwo = "'sha256-Zv+Ks1jtSGkLu5sIuD8jmWs8WNIVjrVm3DGMZqVfwFQ='";

    [Test]
    public void InlineScripts_AreHashedExactlyAsServed_ExternalScriptsAreNot()
    {
        // Raw-string literals use the source file's line endings; the hash expects "\n".
        var html = IndexHtml.ReplaceLineEndings("\n");

        Assert.That(SecurityHeaders.InlineScriptHashes(html), Is.EqualTo(new[] { HashOne, HashTwo }));
    }

    [Test]
    public void Policy_WithIndexHtml_ListsTheHashes()
    {
        Assert.That(SecurityHeaders.ContentSecurityPolicy(IndexHtml.ReplaceLineEndings("\n")), Is.EqualTo(
            $"default-src 'self'; script-src 'self' {HashOne} {HashTwo}; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'"));
    }

    [Test]
    public void Policy_WithoutIndexHtml_IsSelfOnly()
    {
        Assert.That(SecurityHeaders.ContentSecurityPolicy(null), Does.Contain("script-src 'self';"));
    }

    [TestCase("/emulator/play.html")]
    [TestCase("/Emulator/x")]
    [TestCase("/emulator")]
    public async Task EmulatorPaths_GetTheEmulatorPolicy(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Is.EqualTo(
            "default-src 'self'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval' blob:; worker-src 'self' blob:; connect-src 'self' blob: data:; "
            + "img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'self'"));
    }

    [TestCase("/")]
    [TestCase("/api/systems")]
    [TestCase("/emulatorx")]
    public async Task OtherPaths_KeepTheAppPolicy(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(),
            Does.StartWith("default-src 'self'; script-src 'self'").And.EndWith("frame-ancestors 'none'").And.Not.Contain("unsafe-eval"));
    }

    /// <summary>A host serving the real emulator pages and a vendored script from a temp web root, plus the SPA index.</summary>
    private static (ApiFactory Factory, HttpClient Client) EmulatorSite()
    {
        var factory = new ApiFactory();
        var webRoot = Path.Combine(factory.Root, "wwwroot");
        var emulator = Path.Combine(webRoot, "emulator");
        Directory.CreateDirectory(Path.Combine(emulator, "vendor", "data"));
        var source = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "src", "RnRomManager.Web", "static", "emulator");
        foreach (var page in new[] { "play.html", "play-psp.html", "play.css" })
            File.Copy(Path.Combine(source, page), Path.Combine(emulator, page));
        File.WriteAllText(Path.Combine(emulator, "vendor", "data", "loader.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(webRoot, "index.html"), IndexHtml.ReplaceLineEndings("\n"));
        factory.Settings[WebHostDefaults.WebRootKey] = webRoot;
        return (factory, factory.CreateClient());
    }

    [Test]
    public async Task ThePspDocument_IsCrossOriginIsolated_AndCannotBeFramed()
    {
        var (factory, client) = EmulatorSite();
        using var _ = factory;
        using var __ = client;

        using var response = await client.GetAsync("/emulator/play-psp.html");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("play.css").And.Contain("play.js"));
        Assert.That(response.Headers.GetValues("Cross-Origin-Opener-Policy").Single(), Is.EqualTo("same-origin"));
        Assert.That(response.Headers.GetValues("Cross-Origin-Embedder-Policy").Single(), Is.EqualTo("require-corp"));
        Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Is.EqualTo(
            "default-src 'self'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval' blob:; worker-src 'self' blob:; connect-src 'self' blob: data:; "
            + "img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'"));
        Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
    }

    [TestCase("/emulator/play.html", "frame-ancestors 'self'")]
    [TestCase("/emulator/play.css", "frame-ancestors 'self'")]
    [TestCase("/emulator/vendor/data/loader.js", "frame-ancestors 'self'")]
    [TestCase("/", "frame-ancestors 'none'")]
    [TestCase("/api/systems", "frame-ancestors 'none'")]
    public async Task OtherResponses_AreNotIsolated(string path, string frameAncestors)
    {
        var (factory, client) = EmulatorSite();
        using var _ = factory;
        using var __ = client;

        using var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.InternalServerError), path);
        Assert.That(response.Headers.Contains("Cross-Origin-Opener-Policy"), Is.False, path);
        Assert.That(response.Headers.Contains("Cross-Origin-Embedder-Policy"), Is.False, path);
        Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Does.EndWith(frameAncestors), path);
    }

    [Test]
    public async Task TheModalPage_StillLoadsItsSharedStyles()
    {
        var (factory, client) = EmulatorSite();
        using var _ = factory;
        using var __ = client;

        var page = await client.GetStringAsync("/emulator/play.html");
        using var css = await client.GetAsync("/emulator/play.css");

        Assert.That(page, Does.Contain("<link rel=\"stylesheet\" href=\"play.css\" />").And.Not.Contain("<style"));
        Assert.That(css.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(css.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/css"));
    }

    [TestCase("/health/live")]
    [TestCase("/api/nope")]
    [TestCase("/api/auth/me")]
    [TestCase("/")]
    [TestCase("/_app/immutable/x.js")]
    public async Task EveryResponse_CarriesTheHeaders(string path)
    {
        using var factory = new ApiFactory();
        var webRoot = Path.Combine(factory.Root, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "_app", "immutable"));
        File.WriteAllText(Path.Combine(webRoot, "index.html"), IndexHtml.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(webRoot, "_app", "immutable", "x.js"), "console.log(1);");
        factory.Settings[WebHostDefaults.WebRootKey] = webRoot;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Does.Contain(HashOne).And.Contain(HashTwo));
        Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
        Assert.That(response.Headers.GetValues("Referrer-Policy").Single(), Is.EqualTo("no-referrer"));
    }
}
