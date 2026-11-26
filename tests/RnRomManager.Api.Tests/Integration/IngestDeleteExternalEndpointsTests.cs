using System.Net;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec G4 through the API: an external system's ingest files behind a link are neither listed nor deleted.</summary>
[Category("Integration")]
public class IngestDeleteExternalEndpointsTests
{
    [Test]
    public async Task LinkedFailedFolder_ListsNothing_AndDeleteReportsTheFileAsFailed()
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>(services),
        };
        using var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var outside = Directory.CreateDirectory(Path.Combine(factory.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "a.nsp"), "theirs");
        var link = Path.Combine(factory.Root, "ingest", ExternalTestHandler.SystemId, "_failed");
        Directory.Delete(link);
        Assert.That(TestLinks.TryLinkDirectory(link, outside), Is.True, "this account can't create a directory link");
        try
        {
            using var list = await admin.GetAsync("/api/ingest/failed");
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await list.Content.ReadAsStringAsync(), Does.Not.Contain("a.nsp"));

            using var response = await admin.PostAsync("/api/ingest/delete",
                new { files = new[] { new { system = ExternalTestHandler.SystemId, folder = "failed", name = "a.nsp" } } });
            var body = await AuthClient.JsonAsync(response);

            Assert.That((body.GetProperty("deleted").GetInt32(), body.GetProperty("failed").GetArrayLength()), Is.EqualTo((0, 1)));
            Assert.That(File.ReadAllText(Path.Combine(outside, "a.nsp")), Is.EqualTo("theirs"));
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link); // or the factory's cleanup would follow it
        }
    }
}
