using RnRomManager.Core.Library;
using RnRomManager.Core.Media;

namespace RnRomManager.Core.Tests.Library;

public class GameImagesTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static InfoJsonImage Image(string name, ImageType type, string source = InfoJsonImage.SourceLibretro, bool cover = false) => new()
    {
        FileName = name, Type = type, Source = source, IsDefault = source == InfoJsonImage.SourceDefault, IsCover = cover,
        SizeBytes = 1, Sha1 = new string('a', 40), Width = 1, Height = 1, DateAdded = When,
    };

    private static InfoJsonImage Placeholder(bool cover = true) => Image("boxart-01.png", ImageType.BoxArt, InfoJsonImage.SourceDefault, cover);

    private static MediaImage Fetched(ImageType type, bool isDefault = false) =>
        new(type, [(byte)type], 2, 2, isDefault ? null : "https://x/" + type, isDefault);

    private static MediaResult Found(bool realBoxArt, bool snap) =>
        new(Fetched(ImageType.BoxArt, isDefault: !realBoxArt), snap ? Fetched(ImageType.Snap) : null);

    [Test]
    public void NextName_LowestFreeNumberPerPrefix_IgnoringCaseAndExtension()
    {
        Assert.That(GameImages.NextName([], ImageType.BoxArt, "png"), Is.EqualTo("boxart-01.png"));
        Assert.That(GameImages.NextName(["BOXART-01.PNG", "boxart-03.jpg", "snap-02.png"], ImageType.BoxArt, "jpg"), Is.EqualTo("boxart-02.jpg"));
        Assert.That(GameImages.NextName(["boxart-01.png", "snap-01.jpg"], ImageType.Snap, "png"), Is.EqualTo("snap-02.png"));
    }

    [Test]
    public void ForNewGame_TheBoxArtIsTheCover()
    {
        var images = GameImages.ForNewGame(Found(realBoxArt: false, snap: true), When);

        Assert.That(images.Select(i => (i.FileName, i.IsCover, i.IsDefault)),
            Is.EqualTo(new[] { ("boxart-01.png", true, true), ("snap-01.png", false, false) }));
    }

    [Test]
    public void ApplyFetched_RealBoxArtReplacesThePlaceholder_KeepingItsCoverStatus()
    {
        var snapCover = Image("snap-01.png", ImageType.Snap, cover: true);
        var (images, entries) = GameImages.ApplyFetched([Placeholder(cover: false), snapCover], Found(realBoxArt: true, snap: true), When)!.Value;

        Assert.That(images.Select(i => (i.FileName, i.IsDefault, i.IsCover, i.Source)), Is.EqualTo(new[]
        {
            ("boxart-01.png", false, false, InfoJsonImage.SourceLibretro), ("snap-01.png", false, true, InfoJsonImage.SourceLibretro),
        }), "no second snap: the game already has one");
        Assert.That(entries.Keys, Is.EqualTo(new[] { "boxart-01.png" }));

        var (asCover, _) = GameImages.ApplyFetched([Placeholder()], Found(realBoxArt: true, snap: false), When)!.Value;
        Assert.That(asCover.Single().IsCover, Is.True);
    }

    [Test]
    public void ApplyFetched_NeverTouchesUploadedOrFetchedImages()
    {
        var uploaded = Image("boxart-02.jpg", ImageType.BoxArt, InfoJsonImage.SourceUpload, cover: true);
        var snap = Image("snap-01.png", ImageType.Snap);

        Assert.That(GameImages.ApplyFetched([uploaded, snap], Found(realBoxArt: true, snap: true), When), Is.Null);
    }

    [Test]
    public void ApplyFetched_AddsASnapOnlyWhenThereIsNone()
    {
        var uploaded = Image("boxart-01.png", ImageType.BoxArt, InfoJsonImage.SourceUpload, cover: true);

        var (images, entries) = GameImages.ApplyFetched([uploaded], Found(realBoxArt: true, snap: true), When)!.Value;

        Assert.That(images.Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("boxart-01.png", true), ("snap-01.png", false) }),
            "the real box art isn't added: it only replaces a placeholder");
        Assert.That(entries.Keys, Is.EqualTo(new[] { "snap-01.png" }));
    }

    [Test]
    public void ApplyFetched_AGameWithNoImages_GetsTheBoxArtAsCover_EvenThePlaceholder()
    {
        var (images, _) = GameImages.ApplyFetched([], Found(realBoxArt: false, snap: true), When)!.Value;

        Assert.That(images.Select(i => (i.FileName, i.IsCover, i.IsDefault)),
            Is.EqualTo(new[] { ("boxart-01.png", true, true), ("snap-01.png", false, false) }));
    }

    [Test]
    public void ApplyFetched_PlaceholderStays_WhenNothingRealWasFound()
    {
        Assert.That(GameImages.ApplyFetched([Placeholder(), Image("snap-01.png", ImageType.Snap)], Found(realBoxArt: false, snap: false), When), Is.Null);
    }

    [Test]
    public void Merge_RenamesCopies_KeepsTheTargetCover_DropsTheSourcePlaceholder()
    {
        var into = new[] { Image("boxart-01.png", ImageType.BoxArt, cover: true), Image("snap-01.png", ImageType.Snap) };
        var from = new[] { Placeholder(), Image("snap-01.jpg", ImageType.Snap, InfoJsonImage.SourceUpload), Image("boxart-01.png", ImageType.BoxArt, cover: false) };

        var (images, copies) = GameImages.Merge(into, from)!.Value;

        Assert.That(images.Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[]
        {
            ("boxart-01.png", true), ("snap-01.png", false), ("snap-02.jpg", false), ("boxart-02.png", false),
        }));
        Assert.That(copies.Select(c => (c.SourceName, c.Copy.FileName)), Is.EqualTo(new[] { ("snap-01.jpg", "snap-02.jpg"), ("boxart-01.png", "boxart-02.png") }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Merge_ARealSourceBoxArtRemovesTheTargetPlaceholder(bool placeholderWasCover)
    {
        var snap = Image("snap-01.png", ImageType.Snap, cover: !placeholderWasCover);
        var from = new[] { Image("boxart-01.png", ImageType.BoxArt, cover: true), Image("boxart-02.jpg", ImageType.BoxArt, InfoJsonImage.SourceUpload) };

        var (images, _) = GameImages.Merge([Placeholder(placeholderWasCover), snap], from)!.Value;

        Assert.That(images.Any(i => i.IsDefault), Is.False);
        Assert.That(images.Single(i => i.IsCover).FileName, Is.EqualTo(placeholderWasCover ? "boxart-02.png" : "snap-01.png"),
            "the first copied box art by new name (boxart-02.png before boxart-03.jpg), or the target's own cover");
    }

    [Test]
    public void Merge_IntoAGameWithNoImages_TheFirstCopyIsTheCover()
    {
        var (images, _) = GameImages.Merge([], [Image("snap-01.png", ImageType.Snap), Image("boxart-01.png", ImageType.BoxArt)])!.Value;

        Assert.That(images.Select(i => (i.FileName, i.IsCover)), Is.EqualTo(new[] { ("snap-01.png", true), ("boxart-01.png", false) }));
    }

    [Test]
    public void Merge_OverTwentyImages_IsRefused()
    {
        var into = Enumerable.Range(1, 15).Select(n => Image($"snap-{n:00}.png", ImageType.Snap, cover: n == 1)).ToArray();
        var from = Enumerable.Range(1, 6).Select(n => Image($"snap-{n:00}.png", ImageType.Snap)).ToArray();

        Assert.That(GameImages.Merge(into, from), Is.Null);
        Assert.That(GameImages.Merge(into, from[..5]), Is.Not.Null, "exactly 20 is fine");
    }

    // T8: fetched images are named by their format; titledb media is the only JPEG source, built here directly.
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    private static MediaImage Jpeg(ImageType type) =>
        new(type, JpegBytes, 64, 32, "https://img-eshop.cdn.nintendo.net/i/" + type, IsDefault: false, InfoJsonImage.SourceTitleDb);

    private static MediaResult JpegFound(bool snap = true) => new(Jpeg(ImageType.BoxArt), snap ? Jpeg(ImageType.Snap) : null);

    [Test]
    public void ForNewGame_JpegMedia_IsNamedJpg_WithItsSource()
    {
        var images = GameImages.ForNewGame(JpegFound(), When);

        Assert.That(images.Select(i => (i.FileName, i.Source, i.SourceUrl, i.IsCover)), Is.EqualTo(new[]
        {
            ("boxart-01.jpg", InfoJsonImage.SourceTitleDb, (string?)"https://img-eshop.cdn.nintendo.net/i/BoxArt", true),
            ("snap-01.jpg", InfoJsonImage.SourceTitleDb, "https://img-eshop.cdn.nintendo.net/i/Snap", false),
        }));
    }

    [Test]
    public void ForNewGame_PngMedia_KeepsPng() =>
        Assert.That(GameImages.ForNewGame(Found(realBoxArt: true, snap: true), When).Select(i => i.FileName), Is.EqualTo(new[] { "boxart-01.png", "snap-01.png" }));

    [Test]
    public void ApplyFetched_JpegReplacingThePlaceholder_KeepsItsNumberAndCover()
    {
        var current = new[] { Image("boxart-01.png", ImageType.BoxArt), Image("boxart-02.png", ImageType.BoxArt, InfoJsonImage.SourceDefault, cover: true) };

        var (images, entries) = GameImages.ApplyFetched(current, JpegFound(snap: false), When)!.Value;

        Assert.That(images.Select(i => (i.FileName, i.IsCover, i.IsDefault)), Is.EqualTo(new[] { ("boxart-01.png", false, false), ("boxart-02.jpg", true, false) }));
        Assert.That(entries.Keys, Is.EqualTo(new[] { "boxart-02.jpg" }));
    }

    [Test]
    public void ApplyFetched_RenamedPlaceholderColliding_TakesTheNextName_AndKeepsTheOtherImage()
    {
        var uploaded = Image("BOXART-01.JPG", ImageType.BoxArt, InfoJsonImage.SourceUpload);
        var current = new[] { Placeholder(cover: true), uploaded };

        var (images, entries) = GameImages.ApplyFetched(current, JpegFound(snap: false), When)!.Value;

        Assert.That(images.Select(i => i.FileName), Is.EqualTo(new[] { "boxart-02.jpg", "BOXART-01.JPG" }));
        Assert.That(images[0].IsCover, Is.True, "the placeholder's cover status");
        Assert.That(images[1], Is.EqualTo(uploaded));
        Assert.That(entries.Keys, Is.EqualTo(new[] { "boxart-02.jpg" }));
    }

    [Test]
    public void ApplyFetched_JpegSnap_ReservesItsNumberAcrossFormats()
    {
        var current = new[] { Image("boxart-01.png", ImageType.BoxArt, cover: true), Image("snap-01.png", ImageType.BoxArt) }; // a box art named like a snap

        var (images, entries) = GameImages.ApplyFetched(current, JpegFound(), When)!.Value;

        Assert.That(images.Last().FileName, Is.EqualTo("snap-02.jpg"));
        Assert.That(entries.Keys, Is.EqualTo(new[] { "snap-02.jpg" }));
    }

    [Test]
    public void ApplyFetched_AnExistingSnap_SuppressesASecond() =>
        Assert.That(GameImages.ApplyFetched([Image("boxart-01.png", ImageType.BoxArt, cover: true), Image("snap-01.png", ImageType.Snap)], JpegFound(), When), Is.Null);

    [Test]
    public void ApplyFetched_NoImages_TakesTheJpegAsCover()
    {
        var (images, _) = GameImages.ApplyFetched([], JpegFound(snap: false), When)!.Value;

        Assert.That(images.Single().FileName, Is.EqualTo("boxart-01.jpg"));
        Assert.That(images.Single().IsCover, Is.True);
    }
}
