using System.Net;
using System.Text;
using System.Text.Json;
using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Bundle Creator B4: replace and delete at a revision.</summary>
public partial class BundleQueueEndpointsTests
{
    private static async Task<string> IdAsync(AuthClient client, string name, params object[] entries) =>
        (await CreateAsync(client, name, entries)).GetProperty("id").GetString()!;

    private static async Task<JsonElement> LoadAsync(AuthClient client, string id)
    {
        using var response = await client.GetAsync($"/api/bundle-queues/{id}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await AuthClient.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> PutAsync(AuthClient client, string id, object body) =>
        client.SendAsync(HttpMethod.Put, $"/api/bundle-queues/{id}", body, sendXsrf: true);

    private static Task<HttpResponseMessage> DeleteAsync(AuthClient client, string id, string? revision) =>
        client.SendAsync(HttpMethod.Delete, revision is null ? $"/api/bundle-queues/{id}" : $"/api/bundle-queues/{id}?revision={revision}",
            null, sendXsrf: true);

    private static string[] FileNames(JsonElement snapshot) =>
        snapshot.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("fileName").GetString()!).ToArray();

    [Test]
    public async Task Replace_AtTheRevision_OverwritesEverything_AndIncrementsIt()
    {
        var id = await IdAsync(_alice, "Old", MainEntry());

        using var replaced = await PutAsync(_alice, id, new
        {
            name = " New ",
            entries = new[] { SetEntry("b.bin"), SetEntry("a.bin") },
            revision = 1,
        });

        Assert.That(replaced.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(replaced);
        Assert.That((body.GetProperty("name").GetString(), body.GetProperty("revision").GetInt32()), Is.EqualTo(("New", 2)));
        var loaded = await LoadAsync(_alice, id);
        Assert.That(loaded.ToString(), Is.EqualTo(body.ToString()));
        Assert.That(FileNames(loaded), Is.EqualTo(new[] { "b.bin", "a.bin" }));

        using (var again = await PutAsync(_alice, id, new { name = "New", entries = Array.Empty<object>(), revision = 2 }))
            Assert.That((await AuthClient.JsonAsync(again)).GetProperty("revision").GetInt32(), Is.EqualTo(3));
        using var list = await _alice.GetAsync("/api/bundle-queues");
        Assert.That((await AuthClient.JsonAsync(list))[0].GetProperty("itemCount").GetInt32(), Is.Zero);
    }

    [Test]
    public async Task Replace_KeepsUnresolvedIdentities()
    {
        var id = await IdAsync(_alice, "Mine");

        using var replaced = await PutAsync(_alice, id, new { name = "Mine", entries = new[] { MainEntry(), SetEntry("gone.bin") }, revision = 1 });

        Assert.That(replaced.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(FileNames(await LoadAsync(_alice, id)), Is.EqualTo(new[] { "Orbit Blocks (World).gb", "gone.bin" }));
    }

    [Test]
    public async Task Replace_WithAStaleRevision_Is409_AndLeavesTheQueue()
    {
        var id = await IdAsync(_alice, "Mine", MainEntry());
        (await PutAsync(_alice, id, new { name = "Mine", entries = new[] { SetEntry("newer.bin") }, revision = 1 })).Dispose();

        using var stale = await PutAsync(_alice, id, new { name = "Mine", entries = new[] { SetEntry("older.bin") }, revision = 1 });

        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(stale), Is.EqualTo("BundleQueueChanged"));
        var loaded = await LoadAsync(_alice, id);
        Assert.That((loaded.GetProperty("revision").GetInt32(), FileNames(loaded)[0]), Is.EqualTo((2, "newer.bin")));
    }

    [Test]
    public async Task Replace_ChecksOwnership_ThenRevision_ThenName()
    {
        await IdAsync(_alice, "Taken");
        var id = await IdAsync(_alice, "Mine");

        using (var foreign = await PutAsync(_bob, id, new { name = "Taken", entries = Array.Empty<object>(), revision = 9 }))
            Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var stale = await PutAsync(_alice, id, new { name = "TAKEN", entries = Array.Empty<object>(), revision = 9 }))
            Assert.That(await AuthClient.CodeAsync(stale), Is.EqualTo("BundleQueueChanged"));
        using (var taken = await PutAsync(_alice, id, new { name = "TAKEN", entries = Array.Empty<object>(), revision = 1 }))
            Assert.That(await AuthClient.CodeAsync(taken), Is.EqualTo("BundleQueueNameTaken"));
        using (var ownName = await PutAsync(_alice, id, new { name = "MINE", entries = Array.Empty<object>(), revision = 1 }))
            Assert.That(ownName.StatusCode, Is.EqualTo(HttpStatusCode.OK), "its own name in another case");
        using (var missing = await PutAsync(_alice, Guid.NewGuid().ToString(), new { name = "x", entries = Array.Empty<object>(), revision = 1 }))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Replace_Malformed_Is400_AndLeavesTheQueue()
    {
        var id = await IdAsync(_alice, "Mine", MainEntry());

        async Task<string[]> FieldsAsync(object body)
        {
            using var response = await PutAsync(_alice, id, body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            return (await AuthClient.JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();
        }

        Assert.That(await FieldsAsync(new { name = "Mine", entries = Array.Empty<object>() }), Is.EqualTo(new[] { "revision" }));
        Assert.That(await FieldsAsync(new { name = "Mine", entries = Array.Empty<object>(), revision = 0 }), Is.EqualTo(new[] { "revision" }));
        Assert.That(await FieldsAsync(new { name = "", entries = new[] { MainEntry(), MainEntry() }, revision = 1 }),
            Is.EquivalentTo(new[] { "name", "entries[1]" }));
        var loaded = await LoadAsync(_alice, id);
        Assert.That((loaded.GetProperty("revision").GetInt32(), FileNames(loaded).Length), Is.EqualTo((1, 1)));
    }

    [Test]
    public async Task ConcurrentReplaces_AtOneRevision_OnlyOneLands()
    {
        var id = await IdAsync(_alice, "Mine");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            PutAsync(_alice, id, new { name = "Mine", entries = new[] { SetEntry($"f{i}.bin") }, revision = 1 })));
        try
        {
            Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.OK), Is.EqualTo(1));
            Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(4));
            var winner = await AuthClient.JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
            Assert.That((await LoadAsync(_alice, id)).ToString(), Is.EqualTo(winner.ToString()));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Test]
    public async Task Replace_OversizedBody_Is413()
    {
        var id = await IdAsync(_alice, "Mine");
        var json = $$"""{ "name": "x", "entries": [], "revision": 1, "pad": "{{new string('a', BundleQueueEndpoints.RequestLimit)}}" }""";

        using var response = await _alice.SendContentAsync(HttpMethod.Put, $"/api/bundle-queues/{id}", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That((await LoadAsync(_alice, id)).GetProperty("revision").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task Delete_NeedsTheCurrentRevision()
    {
        var id = await IdAsync(_alice, "Mine", MainEntry());
        (await PutAsync(_alice, id, new { name = "Mine", entries = Array.Empty<object>(), revision = 1 })).Dispose();

        foreach (var bad in new[] { null, "", "x", "0", "-1", "1.0" })
            using (var invalid = await DeleteAsync(_alice, id, bad))
                Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), $"revision={bad}");
        using (var stale = await DeleteAsync(_alice, id, "1"))
            Assert.That(await AuthClient.CodeAsync(stale), Is.EqualTo("BundleQueueChanged"));
        Assert.That((await LoadAsync(_alice, id)).GetProperty("revision").GetInt32(), Is.EqualTo(2));

        using (var deleted = await DeleteAsync(_alice, id, "2"))
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using (var gone = await _alice.GetAsync($"/api/bundle-queues/{id}"))
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var again = await DeleteAsync(_alice, id, "2"))
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AnotherUser_CannotReplaceOrDelete_EvenWithTheRightRevision()
    {
        var id = await IdAsync(_alice, "Mine", MainEntry());

        using (var put = await PutAsync(_bob, id, new { name = "Stolen", entries = Array.Empty<object>(), revision = 1 }))
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var delete = await DeleteAsync(_bob, id, "1"))
            Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var loaded = await LoadAsync(_alice, id);
        Assert.That((loaded.GetProperty("name").GetString(), loaded.GetProperty("revision").GetInt32(), FileNames(loaded).Length),
            Is.EqualTo(("Mine", 1, 1)));
    }
}
