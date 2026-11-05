using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// The §15.1 "API" row (S38): one expected role for <b>every</b> mapped <c>/api</c> endpoint. Fails when an endpoint has
/// no entry (or an entry has no endpoint), then checks each one: anonymous → 401 unless anonymous is allowed, a
/// <c>User</c> → 403 on Admin endpoints and through on the rest.
/// </summary>
[Category("Integration")]
public partial class EndpointRoleMatrixTests
{
    private enum Role
    {
        Anonymous,
        User,
        Admin,
    }

    private static readonly Dictionary<string, Role> Expected = new(StringComparer.Ordinal)
    {
        ["GET /api/auth/antiforgery"] = Role.Anonymous,
        ["POST /api/auth/login"] = Role.Anonymous,
        ["POST /api/auth/logout"] = Role.User,
        ["GET /api/auth/me"] = Role.User,
        ["POST /api/auth/change-password"] = Role.User,

        ["GET /api/users"] = Role.Admin,
        ["POST /api/users"] = Role.Admin,
        ["PATCH /api/users/{id:int}"] = Role.Admin,
        ["POST /api/users/{id:int}/reset-password"] = Role.Admin,

        ["GET /api/systems"] = Role.User,
        ["GET /api/games"] = Role.User,
        ["GET /api/games/{id:guid}"] = Role.User,
        ["GET /api/games/{id:guid}/images/{imageId:long}"] = Role.User,
        ["GET /api/games/{id:guid}/download"] = Role.User,
        ["GET /api/games/{id:guid}/roms/{romId:long}/download"] = Role.User,
        ["PATCH /api/games/{id:guid}"] = Role.Admin,
        ["PATCH /api/games/{id:guid}/rating"] = Role.User,
        ["DELETE /api/games/{id:guid}"] = Role.Admin,
        ["DELETE /api/games/{id:guid}/roms/{romId:long}"] = Role.Admin,
        ["POST /api/games/{id:guid}/roms/{romId:long}/move"] = Role.Admin,
        ["POST /api/games/{id:guid}/merge"] = Role.Admin,
        ["POST /api/games/{id:guid}/media/retry"] = Role.Admin,

        ["GET /api/ingest/pending"] = Role.Admin,
        ["POST /api/jobs/ingest"] = Role.Admin,
        ["POST /api/jobs/integrity"] = Role.Admin,
        ["POST /api/jobs/dat-refresh"] = Role.Admin,
        ["POST /api/jobs/media-retry"] = Role.Admin,
        ["GET /api/jobs/current"] = Role.Admin,
        ["GET /api/jobs"] = Role.Admin,
        ["GET /api/jobs/{id:guid}"] = Role.Admin,
        ["GET /api/jobs/{id:guid}/items"] = Role.Admin,
        ["POST /api/jobs/{id:guid}/cancel"] = Role.Admin,

        ["GET /api/review"] = Role.Admin,
        ["GET /api/review/{id:long}"] = Role.Admin,
        ["POST /api/review/{id:long}/resolve"] = Role.Admin,
        ["POST /api/review/{id:long}/dismiss"] = Role.Admin,
        ["GET /api/dat/search"] = Role.Admin,

        ["GET /api/integrity/status"] = Role.Admin,
        ["GET /api/integrity/issues"] = Role.Admin,

        ["GET /api/admin/dats"] = Role.Admin,
        ["GET /api/admin/settings"] = Role.Admin,
        ["GET /api/admin/logs"] = Role.Admin,
        ["GET /api/about"] = Role.User,
    };

    [GeneratedRegex(@"\{(\w+):(\w+)\}")]
    private static partial Regex RouteParameter();

    [Test]
    public async Task EveryApiEndpoint_HasAnExpectedRole_AndEnforcesIt()
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        using var anonymous = await AuthClient.CreateAsync(factory);
        using var user = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        var mapped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal) && e.RoutePattern.RawText != "/api/{**rest}")
            // A group root ("") maps as "/api/games/".
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText!.TrimEnd('/')}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(mapped.Except(Expected.Keys), Is.Empty, "mapped endpoints without an expected role");
        Assert.That(Expected.Keys.Except(mapped), Is.Empty, "expected roles for endpoints that aren't mapped");

        // Logout last: it ends the user's session.
        foreach (var (key, role) in Expected.OrderBy(e => e.Key == "POST /api/auth/logout"))
        {
            var method = new HttpMethod(key[..key.IndexOf(' ')]);
            var url = RouteParameter().Replace(key[(key.IndexOf(' ') + 1)..], m => m.Groups[2].Value switch
            {
                "guid" => Guid.NewGuid().ToString(),
                _ => "999999",
            });
            object? body = method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { };

            using var asAnonymous = await anonymous.SendAsync(method, url, body, sendXsrf: true);
            using var asUser = await user.SendAsync(method, url, body, sendXsrf: true);

            if (role == Role.Anonymous)
                Assert.That(asAnonymous.StatusCode, Is.Not.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), key);
            else
                Assert.That(asAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), key);
            if (role == Role.Admin)
                Assert.That(asUser.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), key);
            else
                Assert.That(asUser.StatusCode, Is.Not.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), key);
        }
    }
}
