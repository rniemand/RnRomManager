using System.Buffers.Binary;
using NSubstitute;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDs.Tests;

public class Nintendo3DsHandlerTests
{
    private static readonly ISystemHandler Handler = new Nintendo3DsHandler();

    [Test]
    public void Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("3ds", "Nintendo 3DS", "3DS", "3ds", "Nintendo - Nintendo 3DS")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".3ds", ".cci", ".cia" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16_384L, 4_294_967_296L)));
        Assert.That((Handler.EmbedRomsInZip, Handler.LegacyEmbeddedRoms, Handler.FileSets, Handler.KeepsSourceExtension),
            Is.EqualTo((false, true, false, false)));
        Assert.That((Handler.DatSource, Handler.DatFolder), Is.EqualTo((DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(new[] { DatKind.Developer, DatKind.Publisher, DatKind.Genre }));
        Assert.That(Handler.BrowserCore, Is.Null);
    }

    [Test]
    public void RomStorage_IsConfigurable_DefaultBesideTheZip_FollowsTheOverride()
    {
        Assert.That((Handler.RomStorageConfigurable, Handler.DefaultEmbedRomsInZip), Is.EqualTo((true, false)));
        var settings = Substitute.For<IRomStorageSettings>();
        ISystemHandler handler = new Nintendo3DsHandler(settings);
        Assert.That(handler.EmbedRomsInZip, Is.False, "no stored value");
        settings.Override("3ds").Returns(true);
        Assert.That((handler.EmbedRomsInZip, handler.DefaultEmbedRomsInZip), Is.EqualTo((true, false)));
    }

    [Test]
    public void ReadHeader_DelegatesToTheReader()
    {
        var rom = new byte[0x200];
        "NCSD"u8.CopyTo(rom.AsSpan(0x100));

        Assert.That(Handler.ReadHeader(rom), Is.EqualTo(Nintendo3DsHeaderReader.Read(rom)).Using<RomHeader>((a, b) =>
            a.ClassifiedSystemId == b.ClassifiedSystemId && a.IsPlausible == b.IsPlausible && a.Fields.SequenceEqual(b.Fields)));
        Assert.That(Handler.ReadHeader(rom)!.Fields["format"], Is.EqualTo("NCSD"));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That(System.Text.Encoding.ASCII.GetString(png[12..16]), Is.EqualTo("IHDR"));
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
