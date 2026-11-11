using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec T8 through Retry media: a titledb game's placeholder is replaced by the CDN's JPEG, named and stored by its format.</summary>
public partial class MediaRetryJobTests
{
    private const string Cdn = "https://img-eshop.cdn.nintendo.net/i/";
    private const string OrbitId = "0100000000002000";
    private static readonly byte[] BoxJpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    /// <summary>Registers the extdb handler and a titledb store whose entry has a CDN box art; returns the cache.</summary>
    private DatCache UseTitleDb()
    {
        var titleDb = new ExternalTitleDbTestHandler();
        ISystemHandler[] all = [.. _systems.All, titleDb];
        _systems.All.Returns(all);
        _systems.ById(ExternalTitleDbTestHandler.SystemId).Returns(titleDb);
        var dats = new DatCache(Options.Create(new PathsOptions()), _systems, NullLogger<DatCache>.Instance);
        dats.Publish(new Dictionary<string, DatStore>
        {
            [titleDb.Id] = new([new(OrbitId, "Orbit", null, null, null, [], [], Cdn + "box.jpg", null)], "2026.11.10"),
        });
        return dats;
    }

    /// <summary>An external extdb game whose only image is the placeholder, as cover.</summary>
    private async Task<GameRow> AddTitleDbGameAsync()
    {
        var placeholder = ExternalTestHandler.FixtureBoxArtPng.ToArray();
        var rom = Rom($"Orbit [{OrbitId}][v0].nsp", RomBytes(++_seed)) with
        {
            ExternalFileName = $"Orbit.Orbit [{OrbitId}][v0].nsp",
            Identification = new InfoJsonIdentification(IdentificationMethod.TitleDb, DatStore.TitleDbName, "2026.11.10", $"Orbit [{OrbitId}]"),
        };
        var image = Image(ImageType.BoxArt, placeholder, isDefault: true);
        var game = Game("Orbit", rom) with { System = ExternalTitleDbTestHandler.SystemId, Images = [image] };
        var zipPath = $"{ExternalTitleDbTestHandler.SystemId}/O/Orbit.zip";
        var path = _dir.Combine(ExternalTitleDbTestHandler.SystemId, "O", "Orbit.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game,
            new Dictionary<string, Func<Stream>> { [LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(placeholder) }));
        return new MirrorEntry(game, zipPath, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)).ToRows().Game;
    }

    [Test]
    public async Task TitleDb_RetryMedia_ReplacesThePlaceholderWithAJpeg_KeepingItsCover()
    {
        var dats = UseTitleDb();
        var row = await AddTitleDbGameAsync();
        _routes[Cdn + "box.jpg"] = () => StubHttpHandler.Bytes(BoxJpeg);
        var retry = new GameMediaRetry(_mirror, _reader, _systems, TestMedia.Create(new HttpClient(_server), BaseUrl, new HttpClient(_server), dats),
            Options.Create(new PathsOptions { Roms = _dir.Path }), _audit, TimeProvider.System, NullLogger<GameMediaRetry>.Instance);

        var result = await retry.RetryAsync(row, new CircuitBreaker(), skipManualOnly: false, new RnRomManager.Core.Audit.AuditActor(null, null, _jobId), default);

        Assert.That(result.Outcome, Is.EqualTo(MediaRetryOutcome.Updated));
        var image = Read(row).Images.Single();
        Assert.That((image.FileName, image.Source, image.SourceUrl, image.IsCover, image.IsDefault),
            Is.EqualTo(("boxart-01.jpg", InfoJsonImage.SourceTitleDb, (string?)(Cdn + "box.jpg"), true, false)));
        Assert.That((image.SizeBytes, image.Sha1), Is.EqualTo(((long)BoxJpeg.Length, Convert.ToHexStringLower(SHA1.HashData(BoxJpeg)))));
        // The ZIP's actual entries: the JPEG under its new name, the placeholder PNG gone.
        using (var zip = ZipFile.OpenRead(_dir.Combine(row.ZipPath.Split('/'))))
        {
            Assert.That(zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("images/")), Is.EqualTo(new[] { "images/boxart-01.jpg" }));
            using var entry = zip.GetEntry("images/boxart-01.jpg")!.Open();
            using var copy = new MemoryStream();
            entry.CopyTo(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(BoxJpeg));
        }
        // What the mirror was given.
        var mirrored = (MirrorEntry)_mirror.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(ILibraryMirror.ReplaceAsync)).GetArguments()[0]!;
        Assert.That(mirrored.ToRows().Images.Select(i => (i.FileName, i.Source, i.SourceUrl)),
            Is.EqualTo(new[] { ("boxart-01.jpg", InfoJsonImage.SourceTitleDb, (string?)(Cdn + "box.jpg")) }));
    }
}
