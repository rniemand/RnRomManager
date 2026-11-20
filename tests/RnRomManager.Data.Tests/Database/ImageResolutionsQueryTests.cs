using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Image Resolutions R3: the report query, against MariaDB.</summary>
[Category("Integration")]
public class ImageResolutionsQueryTests
{
    private MySqlConnectionFactory _factory = null!;
    private ReportRepository _reports = null!;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(_factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _reports = new ReportRepository(_factory);
    }

    private static InfoJsonImage Image(string fileName, ImageType type, int width, int height, bool isDefault = false) => new()
    {
        FileName = fileName, Type = type, Source = isDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceUpload, SourceUrl = null,
        IsDefault = isDefault, IsCover = false, SizeBytes = width * 10L, Sha1 = new string('c', 40), Width = width, Height = height, DateAdded = When,
    };

    /// <summary>Mirrors a game with the given images.</summary>
    private async Task<Guid> Add(string title, string system, params InfoJsonImage[] images)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { System = system, Images = images };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(
            new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private async Task<(string Title, string File)[]> Rows(string? system = null, ImageType? type = null, int? maxWidth = null,
        int? maxHeight = null)
    {
        var page = await _reports.ListImageResolutionsAsync(system, type, maxWidth, maxHeight, 1, 50, default);
        return [.. page.Items.Select(i => (i.DisplayTitle, $"{i.Type} {i.Width}x{i.Height}"))];
    }

    [Test]
    public async Task ListsNonDefaultImages_SmallestAreaFirst_WithEveryField()
    {
        var a = await Add("Alpha", "gb", Image("boxart-01.png", ImageType.BoxArt, 300, 300), Image("snap-01.png", ImageType.Snap, 160, 144),
            Image("boxart-02.png", ImageType.BoxArt, 512, 512, isDefault: true));

        var page = await _reports.ListImageResolutionsAsync(null, null, null, null, 1, 50, default);

        Assert.That(page.Total, Is.EqualTo(2));
        var first = page.Items[0];
        Assert.That(first with { ImageId = 0 }, Is.EqualTo(new Core.Reports.ImageResolutionItem(0, a, "Alpha", "gb", ImageType.Snap, 160, 144, 1600)));
        Assert.That(page.Items[1].Type, Is.EqualTo(ImageType.BoxArt));
    }

    [Test]
    public async Task SystemAndTypeFilters()
    {
        await Add("Alpha", "gb", Image("boxart-01.png", ImageType.BoxArt, 100, 100), Image("title-01.png", ImageType.TitleScreen, 90, 90));
        await Add("Beta", "nes", Image("boxart-01.png", ImageType.BoxArt, 80, 80), Image("other-01.png", ImageType.Other, 70, 70));

        Assert.That(await Rows(system: "gb"), Is.EqualTo(new[] { ("Alpha", "TitleScreen 90x90"), ("Alpha", "BoxArt 100x100") }));
        Assert.That(await Rows(type: ImageType.BoxArt), Is.EqualTo(new[] { ("Beta", "BoxArt 80x80"), ("Alpha", "BoxArt 100x100") }));
        Assert.That(await Rows(system: "nes", type: ImageType.Other), Is.EqualTo(new[] { ("Beta", "Other 70x70") }));
        Assert.That(await Rows(system: "snes"), Is.Empty);
    }

    [Test]
    public async Task MaxWidthAndMaxHeight_InclusiveAndOr()
    {
        await Add("Alpha", "gb", Image("boxart-01.png", ImageType.BoxArt, 100, 400), Image("snap-01.png", ImageType.Snap, 400, 100),
            Image("title-01.png", ImageType.TitleScreen, 401, 401));

        Assert.That(await Rows(maxWidth: 100), Is.EqualTo(new[] { ("Alpha", "BoxArt 100x400") }));
        Assert.That(await Rows(maxWidth: 99), Is.Empty);
        Assert.That(await Rows(maxHeight: 100), Is.EqualTo(new[] { ("Alpha", "Snap 400x100") }));
        Assert.That(await Rows(maxWidth: 100, maxHeight: 100), Is.EquivalentTo(new[] { ("Alpha", "BoxArt 100x400"), ("Alpha", "Snap 400x100") }));
        Assert.That((await Rows()).Length, Is.EqualTo(3));
    }

    [Test]
    public async Task EqualAreas_OrderBySystemThenTitleThenId()
    {
        await Add("Zeta", "gb", Image("boxart-01.png", ImageType.BoxArt, 200, 50), Image("snap-01.png", ImageType.Snap, 50, 200));
        await Add("Alpha", "nes", Image("boxart-01.png", ImageType.BoxArt, 100, 100));
        await Add("Beta", "gb", Image("boxart-01.png", ImageType.BoxArt, 100, 100));
        await Add("Tiny", "nes", Image("boxart-01.png", ImageType.BoxArt, 10, 10));

        var page = await _reports.ListImageResolutionsAsync(null, null, null, null, 1, 50, default);

        Assert.That(page.Items.Select(i => (i.DisplayTitle, i.SystemId)), Is.EqualTo(new[]
        {
            ("Tiny", "nes"), ("Beta", "gb"), ("Zeta", "gb"), ("Zeta", "gb"), ("Alpha", "nes"),
        }));
        var zeta = page.Items.Where(i => i.DisplayTitle == "Zeta").Select(i => i.ImageId).ToList();
        Assert.That(zeta, Is.Ordered);
    }

    [Test]
    public async Task PagingAndTotal()
    {
        await Add("Alpha", "gb", [.. Enumerable.Range(1, 5).Select(i => Image($"snap-{i:00}.png", ImageType.Snap, i * 10, i * 10))]);

        var second = await _reports.ListImageResolutionsAsync(null, null, null, null, 2, 2, default);

        Assert.That(second.Total, Is.EqualTo(5));
        Assert.That(second.Items.Select(i => i.Width), Is.EqualTo(new[] { 30, 40 }));
        Assert.That((await _reports.ListImageResolutionsAsync(null, null, null, null, 4, 2, default)).Items, Is.Empty);
    }
}
