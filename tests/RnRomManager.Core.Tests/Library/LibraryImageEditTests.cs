using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary><see cref="LibraryEditor"/> image edits (I2, I3, I5–I7) against real ZIPs in a temp folder.</summary>
public class LibraryImageEditTests
{
    private static readonly byte[] Placeholder = PngTests.Png(512, 512);
    private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ZipPath = "gb/T/Tetris.zip";

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private IGameRepository _games = null!;
    private IImageRepository _images = null!;
    private ILibraryMirror _mirror = null!;
    private JobLock _lock = null!;
    private LibraryEditor _editor = null!;
    private int _pngSeed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns("gb");
        handler.DatName.Returns("Nintendo - Game Boy");
        handler.DefaultBoxArtPng.Returns(Placeholder);
        var systems = Substitute.For<ISystemRegistry>();
        systems.All.Returns([handler]);
        systems.ById("gb").Returns(handler);
        _reader = new InfoJsonReader([], systems);
        _games = Substitute.For<IGameRepository>();
        _images = Substitute.For<IImageRepository>();
        _mirror = Substitute.For<ILibraryMirror>();
        _lock = new JobLock();
        var paths = Options.Create(new PathsOptions { Roms = _dir.Path, Cache = _dir.Combine("cache") });
        var media = new MediaService(new HttpClient(), Options.Create(new SourcesOptions { ThumbnailBaseUrl = "" }), NullLogger<MediaService>.Instance);
        var retry = new GameMediaRetry(_mirror, _reader, systems, media, paths, TimeProvider.System, NullLogger<GameMediaRetry>.Instance);
        _editor = new LibraryEditor(_lock, _games, Substitute.For<IRomRepository>(), _images, _mirror, _reader,
            new DatCache(paths, systems, NullLogger<DatCache>.Instance), systems, media, retry, paths, TimeProvider.System,
            NullLogger<LibraryEditor>.Instance);
        _pngSeed = 10;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Abs => _dir.Combine("gb", "T", "Tetris.zip");

    private InfoJsonImage Image(string name, ImageType type, byte[] bytes, bool cover = false, bool isDefault = false) => new()
    {
        FileName = name, Type = type, Source = isDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceUpload, IsDefault = isDefault,
        IsCover = cover, SizeBytes = bytes.Length, Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), Width = 1, Height = 1, DateAdded = When,
    };

    private byte[] NewPng() => PngTests.Png((uint)++_pngSeed, 7);

    /// <summary>Writes the game ZIP with these images and stubs the DB rows (image ids 1, 2, … in order).</summary>
    private async Task Seed(params (InfoJsonImage Image, byte[] Bytes)[] images)
    {
        var romBytes = RomBytes(1);
        var rom = Rom("Tetris (World).gb", romBytes);
        var game = Game("Tetris", rom) with { GameId = GameId, Images = images.Select(i => i.Image).ToList() };
        Directory.CreateDirectory(Path.GetDirectoryName(Abs)!);
        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(romBytes) };
        foreach (var (image, bytes) in images)
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(bytes);
        await new LibraryZipWriter(_reader).WriteAsync(Abs, null, new LibraryZipChanges(game, entries));
        var row = new MirrorEntry(game, ZipPath, 1, DateTime.UtcNow).ToRows();
        _games.GetAsync(GameId, Arg.Any<CancellationToken>()).Returns(row.Game);
        for (var i = 0; i < row.Images.Count; i++)
        {
            var imageRow = row.Images[i] with { Id = i + 1 };
            _images.GetAsync(GameId, i + 1, Arg.Any<CancellationToken>()).Returns(imageRow);
        }
    }

    private Task SeedPlaceholder() => Seed((Image("boxart-01.png", ImageType.BoxArt, Placeholder, cover: true, isDefault: true), Placeholder));

    /// <summary>Reads the ZIP back through the reader (so it passed validation) and checks every image entry's bytes.</summary>
    private IReadOnlyList<InfoJsonImage> Stored()
    {
        using var zip = LibraryZip.Open(Abs, _reader);
        Assert.That(zip.InfoJson!.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok), zip.InfoJson.Rule);
        foreach (var image in zip.InfoJson.Game!.Images)
        {
            using var stream = zip.OpenEntry(LibraryZip.ImageEntry(image.FileName), ImageFormats.MaxBytes);
            Assert.That(Convert.ToHexStringLower(SHA1.HashData(stream)), Is.EqualTo(image.Sha1), image.FileName);
        }
        return zip.InfoJson.Game.Images;
    }

    [Test]
    public async Task Upload_BoxArt_ReplacesThePlaceholder_AndBecomesTheCover()
    {
        await SeedPlaceholder();
        var png = PngTests.Png(300, 400);

        var result = await _editor.AddImageAsync(GameId, ImageType.BoxArt, png, default);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(Stored().Select(i => (i.FileName, i.IsCover, i.IsDefault, i.Source, i.Width, i.Height)),
            Is.EqualTo(new[] { ("boxart-01.png", true, false, InfoJsonImage.SourceUpload, 300, 400) }), "the freed name is reused");
        await _mirror.Received(1).ReplaceAsync(Arg.Is<MirrorEntry>(e => e.Info.Images.Count == 1), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_Snap_NeverChangesTheCover_JpegGetsJpg()
    {
        await SeedPlaceholder();

        var result = await _editor.AddImageAsync(GameId, ImageType.Snap, ImageFormatTests.Jpeg(0xC0, 160, 144), default);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(Stored().Select(i => (i.FileName, i.IsCover, i.Width)), Is.EqualTo(new[] { ("boxart-01.png", true, 1), ("snap-01.jpg", false, 160) }));
    }

    [Test]
    public async Task Upload_Naming_LowestFreeNumber_ExtensionFromMagicBytes()
    {
        var a = NewPng();
        var c = NewPng();
        await Seed((Image("boxart-01.png", ImageType.BoxArt, a, cover: true), a), (Image("BOXART-03.PNG", ImageType.BoxArt, c), c));

        Assert.That((await _editor.AddImageAsync(GameId, ImageType.BoxArt, ImageFormatTests.Jpeg(0xC2, 10, 10), default)).Code, Is.Null);

        Assert.That(Stored().Select(i => i.FileName), Is.EqualTo(new[] { "boxart-01.png", "BOXART-03.PNG", "boxart-02.jpg" }));
    }

    [Test]
    public async Task Upload_IntoAGameWithNoImages_BecomesTheCover()
    {
        await Seed();

        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, NewPng(), default)).Code, Is.Null);

        Assert.That(Stored().Single().IsCover, Is.True);
    }

    [Test]
    public async Task Upload_Errors()
    {
        await SeedPlaceholder();

        Assert.That((await _editor.AddImageAsync(GameId, ImageType.TitleScreen, NewPng(), default)).Errors!.Keys, Is.EqualTo(new[] { "type" }));
        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, [], default)).Errors!.Keys, Is.EqualTo(new[] { "file" }));
        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, "GIF89a...."u8.ToArray(), default)).Errors!["file"][0], Does.Contain("PNG or JPEG"));
        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, [0xFF, 0xD8, 0xFF, 0xD9], default)).Errors!["file"][0], Does.Contain("dimensions"));
        var huge = new byte[ImageFormats.MaxBytes + 1];
        PngTests.Png(1, 1).CopyTo(huge, 0);
        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, huge, default)).Errors!["file"][0], Does.Contain("8 MiB"));
        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, Placeholder, default)).Code, Is.EqualTo(LibraryEditResult.DuplicateImage));
        Assert.That((await _editor.AddImageAsync(Guid.NewGuid(), ImageType.Snap, NewPng(), default)).Code, Is.EqualTo(LibraryEditResult.NotFound));
        using (_lock.TryAcquire())
            Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, NewPng(), default)).Code, Is.EqualTo(LibraryEditResult.JobRunning));
        Assert.That(Stored(), Has.Count.EqualTo(1), "nothing was written");
    }

    [Test]
    public async Task Upload_TheTwentyFirstImage_IsTooManyImages()
    {
        var images = Enumerable.Range(1, 20).Select(n =>
        {
            var png = NewPng();
            return (Image($"snap-{n:00}.png", ImageType.Snap, png, cover: n == 1), png);
        }).ToArray();
        await Seed(images);

        Assert.That((await _editor.AddImageAsync(GameId, ImageType.Snap, NewPng(), default)).Code, Is.EqualTo(LibraryEditResult.TooManyImages));
    }

    [Test]
    public async Task Delete_TheCover_PassesToTheFirstBoxArtByName_ElseTheFirstImage()
    {
        var (cover, snap, b3, b2) = (NewPng(), NewPng(), NewPng(), NewPng());
        await Seed((Image("snap-01.png", ImageType.Snap, cover, cover: true), cover), (Image("snap-02.png", ImageType.Snap, snap), snap),
            (Image("boxart-03.png", ImageType.BoxArt, b3), b3), (Image("boxart-02.png", ImageType.BoxArt, b2), b2));

        Assert.That((await _editor.DeleteImageAsync(GameId, 1, default)).Code, Is.Null);
        Assert.That(Stored().Single(i => i.IsCover).FileName, Is.EqualTo("boxart-02.png"));
    }

    [Test]
    public async Task Delete_TheCover_WithoutBoxArt_GivesTheFirstRemainingImage()
    {
        var (a, b, c) = (NewPng(), NewPng(), NewPng());
        await Seed((Image("snap-01.png", ImageType.Snap, a), a), (Image("snap-02.png", ImageType.Snap, b, cover: true), b), (Image("snap-03.png", ImageType.Snap, c), c));

        Assert.That((await _editor.DeleteImageAsync(GameId, 2, default)).Code, Is.Null);
        Assert.That(Stored().Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("snap-01.png", true), ("snap-03.png", false) }));
    }

    [Test]
    public async Task Delete_TheLastImage_ReAddsThePlaceholderAsCover()
    {
        var png = NewPng();
        await Seed((Image("boxart-01.png", ImageType.BoxArt, png, cover: true), png));

        Assert.That((await _editor.DeleteImageAsync(GameId, 1, default)).Code, Is.Null);

        var stored = Stored().Single();
        Assert.That((stored.FileName, stored.IsDefault, stored.IsCover, stored.Sha1),
            Is.EqualTo(("boxart-01.png", true, true, Convert.ToHexStringLower(SHA1.HashData(Placeholder)))));
    }

    [Test]
    public async Task Delete_ANonCover_KeepsTheCover()
    {
        var (a, b) = (NewPng(), NewPng());
        await Seed((Image("boxart-01.png", ImageType.BoxArt, a, cover: true), a), (Image("snap-01.png", ImageType.Snap, b), b));

        Assert.That((await _editor.DeleteImageAsync(GameId, 2, default)).Code, Is.Null);
        Assert.That(Stored().Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("boxart-01.png", true) }));
    }

    [Test]
    public async Task Delete_ThePlaceholder_IsDefaultImage_UnknownIs404_BusyIsJobRunning()
    {
        await SeedPlaceholder();

        Assert.That((await _editor.DeleteImageAsync(GameId, 1, default)).Code, Is.EqualTo(LibraryEditResult.DefaultImage));
        Assert.That((await _editor.DeleteImageAsync(GameId, 99, default)).Code, Is.EqualTo(LibraryEditResult.NotFound));
        using (_lock.TryAcquire())
            Assert.That((await _editor.DeleteImageAsync(GameId, 1, default)).Code, Is.EqualTo(LibraryEditResult.JobRunning));
        Assert.That(Stored(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SetCover_MovesTheFlag_ToExactlyThatImage()
    {
        var (a, b) = (NewPng(), NewPng());
        await Seed((Image("boxart-01.png", ImageType.BoxArt, a, cover: true), a), (Image("snap-01.png", ImageType.Snap, b), b));

        Assert.That((await _editor.SetCoverAsync(GameId, 2, default)).Code, Is.Null);
        Assert.That(Stored().Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("boxart-01.png", false), ("snap-01.png", true) }));
        Assert.That((await _editor.SetCoverAsync(GameId, 99, default)).Code, Is.EqualTo(LibraryEditResult.NotFound));
        using (_lock.TryAcquire())
            Assert.That((await _editor.SetCoverAsync(GameId, 1, default)).Code, Is.EqualTo(LibraryEditResult.JobRunning));
    }
}
