using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B8, B10: the streamed save uploads.</summary>
public partial class SaveEndpointsTests
{
    private string SavesWork => Path.Combine(_factory.Root, "cache", "work", "saves");

    private bool TempFilesLeft => Directory.Exists(SavesWork) && Directory.EnumerateFiles(SavesWork).Any();

    /// <summary>A multipart body with its parts in the given order; a part with a file name is a file part.</summary>
    private static MultipartFormDataContent Form(params (string Name, HttpContent Content, string? FileName)[] parts)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content, fileName) in parts)
        {
            if (fileName is null)
                form.Add(content, name);
            else
                form.Add(content, name, fileName);
        }
        return form;
    }

    private static (string, HttpContent, string?) Field(string name, string value) => (name, new StringContent(value), null);

    private static (string, HttpContent, string?) FilePart(byte[] bytes, string fileName = "save.state")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return ("file", content, fileName);
    }

    private Task<HttpResponseMessage> Upload(Guid gameId, MultipartFormDataContent form) =>
        _user.SendContentAsync(HttpMethod.Post, $"/api/games/{gameId}/saves", form);

    private static MultipartFormDataContent AddForm(long romId, byte[] bytes, string fileName = "save.state", string name = "Before boss") =>
        Form(Field("romId", romId.ToString()), Field("kind", "state"), Field("name", name), FilePart(bytes, fileName));

    [Test]
    public async Task Upload_AsAPlainUser_Is201_WithTheItem_AndTheBytesInTheZip()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var romId = await RomId(game.Id, "Tetris (World).gb");

        using var response = await Upload(game.Id, Form(Field("romId", romId.ToString()), Field("kind", "battery"), Field("name", " Gym 8 "),
            Field("description", "before\nthe league"), FilePart([1, 2, 3], "POKEMON.SRM")));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        var item = await AuthClient.JsonAsync(response);
        Assert.That((item.GetProperty("name").GetString(), item.GetProperty("kind").GetString(), item.GetProperty("description").GetString(),
                item.GetProperty("extension").GetString(), item.GetProperty("sizeBytes").GetInt64(), item.GetProperty("uploadedBy").GetString()),
            Is.EqualTo(("Gym 8", "battery", "before\nthe league", ".srm", 3L, "plain")));
        var id = item.GetProperty("id").GetString()!;
        Assert.That(response.Headers.Location?.ToString(), Is.EqualTo($"/api/games/{game.Id}/saves/{id}"));
        using (var archive = System.IO.Compression.ZipFile.OpenRead(game.Zip))
        using (var entry = archive.GetEntry(LibraryZip.SaveEntry($"plain/{id}.srm"))!.Open())
        using (var copy = new MemoryStream())
        {
            entry.CopyTo(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
        Assert.That(TempFilesLeft, Is.False);
    }

    [Test]
    public async Task Upload_Of40MiB_Succeeds_AboveTheImageFormLimit()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var bytes = new byte[40 * 1024 * 1024];
        new Random(1).NextBytes(bytes.AsSpan(0, 4096));

        using var response = await Upload(game.Id, AddForm(await RomId(game.Id, "Tetris (World).gb"), bytes));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public void BothUploadRoutes_CarryTheRequestSizeLimit()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } text && text.Contains("/saves", StringComparison.Ordinal)
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { } methods && (methods.Contains("POST") || methods.Contains("PUT")))
            .ToList();

        Assert.That(endpoints, Has.Count.EqualTo(2));
        Assert.That(endpoints.Select(e => e.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize),
            Is.All.EqualTo(SaveEndpoints.UploadRequestLimit));
        Assert.That(SaveEndpoints.UploadRequestLimit, Is.EqualTo(256L * 1024 * 1024 + 64 * 1024));
    }

    /// <summary>A body that claims a length without holding it in memory: zeros, produced as read.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public async Task ContentLengthOverTheLimit_Is413_BeforeAnyOtherCheck()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        foreach (var (gameId, kind) in new[] { (game.Id, "state"), (Guid.NewGuid(), "state"), (game.Id, "nonsense") })
        {
            var content = new StreamContent(new ZeroStream(SaveEndpoints.UploadRequestLimit + 1));
            content.Headers.ContentLength = SaveEndpoints.UploadRequestLimit + 1;
            content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary=x{kind}");

            using var response = await _user.SendContentAsync(HttpMethod.Post, $"/api/games/{gameId}/saves", content);

            var body = await AuthClient.JsonAsync(response);
            Assert.That((response.StatusCode, body.GetProperty("code").GetString(), body.GetProperty("title").GetString()),
                Is.EqualTo((HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge", "The save is larger than 256 MiB.")), $"{gameId} {kind}");
        }
    }

    [Test]
    public async Task AChunkedBodyWhoseFilePassesTheLimit_Is413_AndLeavesNoTempFile()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var file = new StreamContent(new ZeroStream(GameSaves.MaxSaveBytes + 1));
        file.Headers.ContentLength = null; // unknown: the multipart body is sent chunked
        var form = Form(Field("romId", (await RomId(game.Id, "Tetris (World).gb")).ToString()), Field("kind", "state"), Field("name", "Big"),
            ("file", file, "big.state"));
        Assert.That(form.Headers.ContentLength, Is.Null, "test setup: no Content-Length");

        using var response = await Upload(game.Id, form);

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge")));
        Assert.That(TempFilesLeft, Is.False);
    }

    private static IEnumerable<TestCaseData> BadForms()
    {
        static TestCaseData Case(string name, Func<long, HttpContent> form, string field) =>
            new TestCaseData(form, field).SetName("Upload400 " + name);
        yield return Case("file before the text fields", id => Form(FilePart([1]), Field("romId", id.ToString()), Field("kind", "state"), Field("name", "S")), "romId");
        yield return Case("description after the file", id => Form(Field("romId", id.ToString()), Field("kind", "state"), Field("name", "S"), FilePart([1]),
            Field("description", "late")), "description");
        yield return Case("an unknown part", id => Form(Field("romId", id.ToString()), Field("color", "red"), Field("kind", "state"), Field("name", "S"), FilePart([1])), "color");
        yield return Case("missing file", id => Form(Field("romId", id.ToString()), Field("kind", "state"), Field("name", "S")), "file");
        yield return Case("a second file", id => Form(Field("romId", id.ToString()), Field("kind", "state"), Field("name", "S"), FilePart([1]), FilePart([2])), "file");
        yield return Case("an empty file", id => Form(Field("romId", id.ToString()), Field("kind", "state"), Field("name", "S"), FilePart([])), "file");
        yield return Case("a bad kind", id => Form(Field("romId", id.ToString()), Field("kind", "photo"), Field("name", "S"), FilePart([1])), "kind");
        yield return Case("an empty name", id => Form(Field("romId", id.ToString()), Field("kind", "state"), Field("name", "  "), FilePart([1])), "name");
        yield return Case("a field over 4 KiB", id => Form(Field("romId", id.ToString()), Field("kind", "state"), Field("name", new string('n', 5000)), FilePart([1])), "name");
        yield return Case("not multipart", _ => new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), "file");
    }

    [TestCaseSource(nameof(BadForms))]
    public async Task Upload_BadForm_Is400(Func<long, HttpContent> form, string field)
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");

        using var response = await _user.SendContentAsync(HttpMethod.Post, $"/api/games/{game.Id}/saves", form(await RomId(game.Id, "Tetris (World).gb")));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await response.Content.ReadAsStringAsync());
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True, field);
        Assert.That(TempFilesLeft, Is.False);
    }

    [Test]
    public async Task Upload_UnknownGameOrAnotherGamesRelease_Is404()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var other = await WriteGame("Pinball", "Pinball (World).gb");

        using var noGame = await Upload(Guid.NewGuid(), AddForm(await RomId(game.Id, "Tetris (World).gb"), [1]));
        using var otherRelease = await Upload(game.Id, AddForm(await RomId(other.Id, "Pinball (World).gb"), [1]));

        Assert.That((noGame.StatusCode, otherRelease.StatusCode), Is.EqualTo((HttpStatusCode.NotFound, HttpStatusCode.NotFound)));
    }

    [Test]
    public async Task Upload_WhileAJobRuns_Is409_BeforeTheFileIsRead()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var romId = await RomId(game.Id, "Tetris (World).gb");
        using var held = Service<JobLock>().TryAcquire();

        using var response = await Upload(game.Id, AddForm(romId, [1, 2]));

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        Assert.That(Directory.Exists(SavesWork) && Directory.EnumerateFileSystemEntries(SavesWork).Any(), Is.False, "nothing streamed");
    }

    [Test]
    public async Task Upload_25thSaveIsFine_The26thIsSaveLimitReached()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        for (var i = 0; i < GameSaves.MaxPerRelease; i++)
            await AddSave(game, "Tetris (World).gb", $"S{i}", [(byte)i]);

        using var response = await Upload(game.Id, AddForm(await RomId(game.Id, "Tetris (World).gb"), [1]));

        await AssertConflict(response, "SaveLimitReached", LimitTitle);
    }

    [Test]
    public async Task Replace_WithANewExtension_RenamesTheEntry()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = await AddSave(game, "Tetris (World).gb", "S", [1]);

        using var response = await _user.SendContentAsync(HttpMethod.Put, $"/api/games/{game.Id}/saves/{save.Id}/file", Form(FilePart([7, 8], "slot.SRM")));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var item = await AuthClient.JsonAsync(response);
        Assert.That((item.GetProperty("extension").GetString(), item.GetProperty("sizeBytes").GetInt64(), item.GetProperty("updatedBy").GetString()),
            Is.EqualTo((".srm", 2L, "plain")));
        Assert.That(SavesEntries(game.Zip).Order(), Is.EqualTo(new[] { LibraryZip.SaveEntry($"plain/{save.Id}.srm"), LibraryZip.SavesIndexEntry }.Order()));
    }

    [Test]
    public async Task Replace_UnknownSave_Is404_AndATextPart_Is400()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = await AddSave(game, "Tetris (World).gb", "S", [1]);

        using var unknown = await _user.SendContentAsync(HttpMethod.Put, $"/api/games/{game.Id}/saves/{GameSaves.NewId()}/file", Form(FilePart([7])));
        using var text = await _user.SendContentAsync(HttpMethod.Put, $"/api/games/{game.Id}/saves/{save.Id}/file", Form(Field("name", "x"), FilePart([7])));

        Assert.That((unknown.StatusCode, text.StatusCode), Is.EqualTo((HttpStatusCode.NotFound, HttpStatusCode.BadRequest)));
    }
}
