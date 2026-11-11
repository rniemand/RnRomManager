using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Systems;

/// <summary>The shared external-storage fixture handler: its contract, and that an isolated registry can load its DAT.</summary>
public class ExternalTestHandlerTests
{
    [Test]
    public void Contract_IsAnExternalSystemOfItsOwnFamily()
    {
        ISystemHandler handler = new ExternalTestHandler();

        Assert.That((handler.Id, handler.Family, handler.DisplayName, handler.ShortName, handler.DatName),
            Is.EqualTo(("ext", "ext", "External Test", "External Test", "Test - External")));
        Assert.That(handler.RomExtensions, Is.EqualTo(new[] { ".nsp", ".xci" }));
        Assert.That((handler.MinRomSizeBytes, handler.MaxRomSizeBytes), Is.EqualTo((1L, 64L * 1024 * 1024)));
        Assert.That(handler.EmbedRomsInZip, Is.False);
        Assert.That(handler.ReadHeader(new byte[16]), Is.Null);
    }

    [Test]
    public void DefaultBoxArt_IsAValid512Png()
    {
        var png = new ExternalTestHandler().DefaultBoxArtPng;

        Assert.That(PngInfo.Read(png.Span), Is.EqualTo(new PngInfo(512, 512)));
        Assert.That(ImageFormats.Detect(png.Span), Is.EqualTo(ImageFormat.Png));
        Assert.That(ExternalTestHandler.FixtureBoxArtPng.ToArray(), Is.EqualTo(png.ToArray()));
    }

    [Test]
    public void IsolatedRegistry_LoadsItsFixtureDat()
    {
        using var dir = new TempDir();
        var registry = new SystemRegistry([new ExternalTestHandler()]);
        var cache = new DatCache(Options.Create(new PathsOptions { Cache = dir.Path }), registry, NullLogger<DatCache>.Instance);
        var rom = new byte[] { 1, 2, 3, 4 };
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [ExternalTestHandler.DatNameValue] = [new FixtureGame("Orbit Test (World)", "Orbit Test (World).nsp", rom)],
        });

        cache.Load();

        var store = cache.For(ExternalTestHandler.SystemId)!;
        Assert.That((store.DatName, store.Version), Is.EqualTo((ExternalTestHandler.DatNameValue, FixtureDatWriter.Version)));
        Assert.That(store.BySha1(Convert.ToHexStringLower(SHA1.HashData(rom)))!.RomName, Is.EqualTo("Orbit Test (World).nsp"));
    }
}
