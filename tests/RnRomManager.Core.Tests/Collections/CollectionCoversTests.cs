using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Collections;

/// <summary>B10–B11 cover files, in a real temp dir.</summary>
public class CollectionCoversTests
{
    private TempDir _dir = null!;
    private CollectionCovers _covers = null!;
    private readonly Guid _id = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _covers = new CollectionCovers(Options.Create(new PathsOptions { Collections = _dir.Path }), NullLogger<CollectionCovers>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string[] Files() => Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order().ToArray()!;

    [Test]
    public async Task WritePngThenJpg_LeavesOnlyTheJpg_AndNoTempFiles()
    {
        Assert.That(await _covers.WriteAsync(_id, ImageFormat.Png, [1, 2], default), Is.EqualTo("png"));
        _covers.DeleteOtherFormat(_id, "png");
        Assert.That(await _covers.WriteAsync(_id, ImageFormat.Jpeg, [3], default), Is.EqualTo("jpg"));
        _covers.DeleteOtherFormat(_id, "jpg");

        Assert.That(Files(), Is.EqualTo(new[] { $"{_id:D}.jpg" }));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(_dir.Path, $"{_id:D}.jpg")), Is.EqualTo(new byte[] { 3 }));
    }

    [Test]
    public async Task Write_ReplacesTheSameFormat()
    {
        await _covers.WriteAsync(_id, ImageFormat.Png, [1], default);
        await _covers.WriteAsync(_id, ImageFormat.Png, [2], default);

        Assert.That(Files(), Is.EqualTo(new[] { $"{_id:D}.png" }));
        await using var stream = _covers.Open(_id, "png")!;
        Assert.That(stream.ReadByte(), Is.EqualTo(2));
    }

    [Test]
    public async Task DeleteAll_RemovesBothFormats_AndOnlyThisCollections()
    {
        var other = Guid.NewGuid();
        await _covers.WriteAsync(_id, ImageFormat.Png, [1], default);
        await _covers.WriteAsync(_id, ImageFormat.Jpeg, [1], default);
        await _covers.WriteAsync(other, ImageFormat.Png, [1], default);

        _covers.DeleteAll(_id);

        Assert.That(Files(), Is.EqualTo(new[] { $"{other:D}.png" }));
    }

    [Test]
    public void Delete_OfAMissingFile_IsANoOp()
    {
        Assert.DoesNotThrow(() => _covers.DeleteAll(_id));
        Assert.DoesNotThrow(() => _covers.Delete(_id, "png"));
    }

    [Test]
    public void Open_MissingFile_IsNull()
    {
        Assert.That(_covers.Open(_id, "png"), Is.Null);
    }
}
