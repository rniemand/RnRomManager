using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.GameBoy.Tests;

public class GameBoyHandlerTests
{
    private static readonly ISystemHandler[] Handlers = [new GameBoyHandler(), new GameBoyColorHandler()];
    private static readonly ISystemHandler[] AllHandlers = [.. Handlers, new GameBoyAdvanceHandler()];

    [Test]
    public void GameBoy_Contract()
    {
        ISystemHandler gb = new GameBoyHandler();

        Assert.That(gb.ShortName, Is.EqualTo("GB"));
        Assert.That((gb.Id, gb.DisplayName, gb.Family, gb.DatName), Is.EqualTo(("gb", "Game Boy", "gameboy", "Nintendo - Game Boy")));
        Assert.That(gb.RomExtensions, Is.EqualTo(new[] { ".gb" }));
    }

    [Test]
    public void GameBoyColor_Contract()
    {
        ISystemHandler gbc = new GameBoyColorHandler();

        Assert.That(gbc.ShortName, Is.EqualTo("GBC"));
        Assert.That((gbc.Id, gbc.DisplayName, gbc.Family, gbc.DatName), Is.EqualTo(("gbc", "Game Boy Color", "gameboy", "Nintendo - Game Boy Color")));
        Assert.That(gbc.RomExtensions, Is.EqualTo(new[] { ".gbc" }));
    }

    [Test]
    public void GameBoyAdvance_Contract()
    {
        ISystemHandler gba = new GameBoyAdvanceHandler();

        Assert.That(gba.ShortName, Is.EqualTo("GBA"));
        Assert.That((gba.Id, gba.DisplayName, gba.Family, gba.DatName), Is.EqualTo(("gba", "Game Boy Advance", "gba", "Nintendo - Game Boy Advance")));
        Assert.That(gba.RomExtensions, Is.EqualTo(new[] { ".gba" }));
        Assert.That((gba.MinRomSizeBytes, gba.MaxRomSizeBytes), Is.EqualTo((0xC0L, 32L * 1024 * 1024)));
    }

    [TestCaseSource(nameof(Handlers))]
    public void Limits_Are0x150To16MiB(ISystemHandler handler)
    {
        Assert.That((handler.MinRomSizeBytes, handler.MaxRomSizeBytes), Is.EqualTo((0x150L, 16L * 1024 * 1024)));
    }

    [TestCaseSource(nameof(AllHandlers))]
    public void DefaultBoxArt_IsA512x512Png(ISystemHandler handler)
    {
        var png = handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That(System.Text.Encoding.ASCII.GetString(png[12..16]), Is.EqualTo("IHDR"));
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }

    [Test]
    public void DefaultBoxArt_DiffersPerSystem()
    {
        Assert.That(AllHandlers.Select(h => Convert.ToHexString(h.DefaultBoxArtPng.Span)).Distinct().Count(), Is.EqualTo(AllHandlers.Length));
    }

    [TestCaseSource(nameof(Handlers))]
    public void ReadHeader_ClassifiesByTheHeaderNotTheHandler(ISystemHandler handler)
    {
        var dualMode = new SyntheticRomBuilder { CgbFlag = 0x80 }.Build();

        Assert.That(handler.ReadHeader(dualMode)!.ClassifiedSystemId, Is.EqualTo("gbc"));
        Assert.That(handler.ReadHeader(new byte[0x14F]), Is.Null);
    }

    [Test]
    public void GameBoyAdvance_ReadHeader_ClassifiesAsGba()
    {
        ISystemHandler gba = new GameBoyAdvanceHandler();

        Assert.That(gba.ReadHeader(new SyntheticGbaRomBuilder { Title = "STARCOURIER" }.Build())!.ClassifiedSystemId, Is.EqualTo("gba"));
        Assert.That(gba.ReadHeader(new byte[0xBF]), Is.Null);
    }

    [TestCaseSource(nameof(AllHandlers))]
    public void NormalizeForHashing_IsTheIdentity(ISystemHandler handler)
    {
        ReadOnlyMemory<byte> rom = new byte[] { 1, 2, 3 };

        Assert.That(handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom.ToArray()));
    }
}
