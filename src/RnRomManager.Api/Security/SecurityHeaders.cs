using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RnRomManager.Api.Security;

/// <summary>
/// spec §9.2 response headers on every response. <c>script-src</c> allows the inline scripts of
/// <c>wwwroot/index.html</c> (SvelteKit's bootstrap) by hash, computed once at startup.
/// </summary>
public static partial class SecurityHeaders
{
    /// <summary>
    /// Browser emulators B7: the vendored EmulatorJS and its play page. <c>'unsafe-eval'</c> is for the 7z worker that
    /// unpacks the cores (Emscripten's <c>cwrap</c>); <c>frame-ancestors 'self'</c> lets the SPA's modal frame it.
    /// </summary>
    public const string EmulatorPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval' blob:; worker-src 'self' blob:; connect-src 'self' blob: data:; "
        + "img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'self'";

    /// <summary>PSP B3: the dedicated top-level PSP player, the only cross-origin isolated document.</summary>
    public const string PspDocumentPath = "/emulator/play-psp.html";

    /// <summary>PSP B3: the emulator policy for the isolated PSP tab, which nothing may frame.</summary>
    public const string PspEmulatorPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval' blob:; worker-src 'self' blob:; connect-src 'self' blob: data:; "
        + "img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
        string? html = null;
        if (index.Exists)
        {
            using var reader = new StreamReader(index.CreateReadStream(), Encoding.UTF8);
            html = reader.ReadToEnd();
        }
        var csp = ContentSecurityPolicy(html);

        app.Use((ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            if (string.Equals(ctx.Request.Path.Value, PspDocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                // PSP B3: SharedArrayBuffer for PPSSPP's threads needs a cross-origin isolated top-level document. Only
                // this document opts in, so the SPA and the modal player keep working as before.
                headers.ContentSecurityPolicy = PspEmulatorPolicy;
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            }
            else
            {
                headers.ContentSecurityPolicy = ctx.Request.Path.StartsWithSegments("/emulator", StringComparison.OrdinalIgnoreCase) ? EmulatorPolicy : csp;
            }
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            return next(ctx);
        });
        return app;
    }

    /// <param name="indexHtml">null when there's no built SPA (API-only dev): <c>script-src 'self'</c>.</param>
    public static string ContentSecurityPolicy(string? indexHtml)
    {
        var scriptSrc = string.Join(' ', ["'self'", .. InlineScriptHashes(indexHtml ?? "")]);
        return $"default-src 'self'; script-src {scriptSrc}; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
    }

    /// <summary>CSP source for each inline <c>&lt;script&gt;</c> body, hashed exactly as served (whitespace included).</summary>
    public static IEnumerable<string> InlineScriptHashes(string html) =>
        InlineScript().Matches(html)
            .Select(m => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))}'")
            .Distinct();

    // shortcut: regex, not an HTML parser; fine for SvelteKit's generated page, revisit if index.html gets hand-written scripts with "</script>" in strings.
    [GeneratedRegex(@"<script(?![^>]*\ssrc\s*=)[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}
