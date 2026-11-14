using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// The §15.1 "API" row (S38): one expected role for <b>every</b> mapped <c>/api</c> endpoint. Fails when an endpoint has
/// no entry (or an entry has no endpoint), then checks each one: anonymous → 401 unless anonymous is allowed; a user
/// with no roles, an <c>admin</c>-only user and a <c>rom-manager</c>-only user each get through exactly the endpoints
/// their roles allow (403 otherwise). Roles don't imply each other (R2).
/// </summary>
[Category("Integration")]
public partial class EndpointRoleMatrixTests
{
    private enum Role
    {
        Anonymous,
        User,
        Admin,
        RomManager,
    }

    private static readonly Dictionary<string, Role> Expected = new(StringComparer.Ordinal)
    {
        ["GET /api/auth/antiforgery"] = Role.Anonymous,
        ["POST /api/auth/login"] = Role.Anonymous,
        ["POST /api/auth/logout"] = Role.User,
        ["GET /api/auth/me"] = Role.User,
        ["GET /api/auth/me/hotkeys"] = Role.User,
        ["PUT /api/auth/me/hotkeys"] = Role.User,
        ["POST /api/auth/change-password"] = Role.User,

        ["GET /api/users"] = Role.Admin,
        ["POST /api/users"] = Role.Admin,
        ["PATCH /api/users/{id:int}"] = Role.Admin,
        ["POST /api/users/{id:int}/reset-password"] = Role.Admin,

        ["GET /api/systems"] = Role.User,
        ["GET /api/games"] = Role.User,
        ["GET /api/games/facets"] = Role.User,
        ["GET /api/games/{id:guid}/zip-entries"] = Role.User,
        ["GET /api/games/{id:guid}/zip-entries/download"] = Role.User,
        ["GET /api/games/{id:guid}"] = Role.User,
        ["GET /api/games/{id:guid}/images/{imageId:long}"] = Role.User,
        ["GET /api/games/{id:guid}/download"] = Role.User,
        ["GET /api/games/{id:guid}/info-json"] = Role.User,
        ["GET /api/games/{id:guid}/collections"] = Role.User,
        ["GET /api/games/{id:guid}/roms/{romId:long}/download"] = Role.User,
        ["GET /api/games/{id:guid}/roms/{romId:long}/files/{fileId:long}/download"] = Role.User,
        ["GET /api/games/{id:guid}/roms/{romId:long}/play-disc"] = Role.User,
        ["GET /api/games/{id:guid}/roms/{romId:long}/note"] = Role.User,
        ["PUT /api/games/{id:guid}/roms/{romId:long}/note"] = Role.User,
        ["DELETE /api/games/{id:guid}/roms/{romId:long}/note"] = Role.User,
        ["PATCH /api/games/{id:guid}"] = Role.RomManager,
        ["PATCH /api/games/{id:guid}/rating"] = Role.User,
        ["DELETE /api/games/{id:guid}"] = Role.RomManager,
        ["DELETE /api/games/{id:guid}/roms/{romId:long}"] = Role.RomManager,
        ["POST /api/games/{id:guid}/roms/{romId:long}/move"] = Role.RomManager,
        ["POST /api/games/{id:guid}/merge"] = Role.RomManager,
        ["POST /api/games/{id:guid}/media/retry"] = Role.RomManager,
        ["POST /api/games/{id:guid}/images"] = Role.RomManager,
        ["POST /api/games/{id:guid}/images/from-url"] = Role.RomManager,
        ["DELETE /api/games/{id:guid}/images/{imageId:long}"] = Role.RomManager,
        ["POST /api/games/{id:guid}/images/{imageId:long}/cover"] = Role.RomManager,

        ["GET /api/emulator/bios/{name}"] = Role.User,
        ["GET /api/emulator/genesis/{name}"] = Role.User,
        ["HEAD /api/emulator/genesis"] = Role.User,

        ["GET /api/collections"] = Role.User,
        ["POST /api/collections"] = Role.User,
        ["GET /api/collections/{id:guid}"] = Role.User,
        ["PATCH /api/collections/{id:guid}"] = Role.User,
        ["DELETE /api/collections/{id:guid}"] = Role.User,
        ["POST /api/collections/{id:guid}/games"] = Role.User,
        ["DELETE /api/collections/{id:guid}/games/{gameId:guid}"] = Role.User,
        ["GET /api/collections/{id:guid}/cover"] = Role.User,
        ["POST /api/collections/{id:guid}/cover"] = Role.User,
        ["POST /api/collections/{id:guid}/cover/from-url"] = Role.User,
        ["DELETE /api/collections/{id:guid}/cover"] = Role.User,

        ["GET /api/ingest/pending"] = Role.Admin,
        ["GET /api/ingest/failed"] = Role.Admin,
        ["POST /api/ingest/delete"] = Role.Admin,
        ["GET /api/ingest/removed"] = Role.Admin,
        ["POST /api/ingest/restore"] = Role.Admin,
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
        ["DELETE /api/review/{id:long}"] = Role.Admin,
        ["POST /api/review/delete"] = Role.Admin,
        ["GET /api/dat/search"] = Role.Admin,

        ["GET /api/integrity/status"] = Role.Admin,
        ["GET /api/integrity/issues"] = Role.Admin,

        ["GET /api/admin/dats"] = Role.Admin,
        ["GET /api/admin/settings"] = Role.Admin,
        ["GET /api/admin/backups"] = Role.Admin,
        ["PUT /api/admin/backups/settings"] = Role.Admin,
        ["POST /api/admin/backups"] = Role.Admin,
        ["GET /api/admin/logs"] = Role.Admin,
        ["GET /api/admin/logs/recent"] = Role.Admin,
        ["GET /api/admin/logs/stream"] = Role.Admin,
        ["GET /api/admin/audit"] = Role.Admin,
        ["GET /api/admin/audit/actions"] = Role.Admin,
        ["GET /api/about"] = Role.User,

        ["GET /api/reports/missing-images"] = Role.User,
        ["GET /api/reports/library-size"] = Role.User,
        ["GET /api/reports/library-size/{system}"] = Role.User,
        ["GET /api/reports/duplicates"] = Role.User,
        ["POST /api/reports/duplicates/dismiss"] = Role.RomManager,
        ["POST /api/reports/duplicates/merge"] = Role.RomManager,
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
        using var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "admin-only", Roles.Of(Roles.Admin));
        (await admin.LoginAsync("admin-only", TestUsers.Password)).Dispose();
        using var manager = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "manager-only", Roles.Of(Roles.RomManager));
        (await manager.LoginAsync("manager-only", TestUsers.Password)).Dispose();

        var mapped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal) && e.RoutePattern.RawText != "/api/{**rest}")
            // A group root ("") maps as "/api/games/".
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText!.TrimEnd('/')}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(mapped.Except(Expected.Keys), Is.Empty, "mapped endpoints without an expected role");
        Assert.That(Expected.Keys.Except(mapped), Is.Empty, "expected roles for endpoints that aren't mapped");

        // Headers only: the log stream never ends on its own. Logout last: it ends the user's session.
        foreach (var (key, role) in Expected.OrderBy(e => e.Key == "POST /api/auth/logout"))
        {
            var method = new HttpMethod(key[..key.IndexOf(' ')]);
            var url = RouteParameter().Replace(key[(key.IndexOf(' ') + 1)..], m => m.Groups[2].Value switch
            {
                "guid" => Guid.NewGuid().ToString(),
                _ => "999999",
            });
            object? body = method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { };

            using var asAnonymous = await anonymous.SendAsync(method, url, body, sendXsrf: true, completion: HttpCompletionOption.ResponseHeadersRead);
            using var asUser = await user.SendAsync(method, url, body, sendXsrf: true, completion: HttpCompletionOption.ResponseHeadersRead);
            using var asAdmin = await admin.SendAsync(method, url, body, sendXsrf: true, completion: HttpCompletionOption.ResponseHeadersRead);
            using var asManager = await manager.SendAsync(method, url, body, sendXsrf: true, completion: HttpCompletionOption.ResponseHeadersRead);

            if (role == Role.Anonymous)
                Assert.That(asAnonymous.StatusCode, Is.Not.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), key);
            else
                Assert.That(asAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), key);
            AssertAllowed(asUser, role is Role.Anonymous or Role.User, $"{key} as a user");
            AssertAllowed(asAdmin, role is not Role.RomManager, $"{key} as admin-only");
            AssertAllowed(asManager, role is not Role.Admin, $"{key} as rom-manager-only");
        }
    }

    private static void AssertAllowed(HttpResponseMessage response, bool allowed, string message)
    {
        if (allowed)
            Assert.That(response.StatusCode, Is.Not.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), message);
        else
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), message);
    }
}
