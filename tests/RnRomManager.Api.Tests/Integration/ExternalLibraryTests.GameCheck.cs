using System.Net;
using System.Net.Http.Headers;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>G1, G4: the first real box art of an external game resolves its <c>DefaultBoxArt</c> issue without a game check.</summary>
public partial class ExternalLibraryTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 1, 0x2C, 0, 0, 1, 0x90,
        8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];

    private Task<long> OpenIssueAsync(string type) => Service<IIntegrityIssueRepository>().InsertAsync(new IntegrityIssue
    {
        Type = type, Severity = IntegrityIssueType.SeverityOf(type), ZipPath = ZipRelative, Status = IntegrityIssue.Open,
        FirstSeenJobId = Guid.NewGuid(), LastSeenJobId = Guid.NewGuid(), DetectedAt = DateTime.UtcNow,
    }, default);

    [Test]
    public async Task FirstRealBoxArt_OfAnExternalGame_ResolvesDefaultBoxArt_WithoutAGameCheck_G1()
    {
        var gameId = await FirstReleaseAsync();
        await OpenIssueAsync(IntegrityIssueType.DefaultBoxArt);
        await OpenIssueAsync(IntegrityIssueType.MissingBoxArt);
        using var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "manager", Roles.Of(Roles.Admin));
        (await client.LoginAsync("manager", TestUsers.Password)).Dispose();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "box.png");
        form.Add(new StringContent("BoxArt"), "type");

        using var response = await client.SendContentAsync(HttpMethod.Post, $"/api/games/{gameId}/images", form);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM jobs WHERE type = 'Integrity' AND mode = 'Game'"), Is.Zero);
        var statuses = (await db.QueryAsync<(string, string)>("SELECT type, status FROM integrity_issues")).ToDictionary(r => r.Item1, r => r.Item2);
        Assert.That(statuses, Is.EquivalentTo(new Dictionary<string, string>
        {
            [IntegrityIssueType.DefaultBoxArt] = IntegrityIssue.Resolved,
            [IntegrityIssueType.MissingBoxArt] = IntegrityIssue.Open,
        }));
    }
}
