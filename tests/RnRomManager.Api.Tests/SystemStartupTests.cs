using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Tests;

/// <summary>Every system handler is registered and startup step 4 creates their folders (spec §5, §22).</summary>
public class SystemStartupTests
{
    [Test]
    public void Startup_RegistersEverySystem_AndCreatesTheirIngestFoldersOnly()
    {
        using var factory = new ApiFactory();
        using var _ = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<ISystemRegistry>();

        Assert.That(registry.All.Select(h => h.Id), Is.EqualTo(new[] { "gb", "gbc", "gba", "n64", "nes", "fds", "snes", "satellaview", "sufami", "vb", "a2600", "a5200", "a7800", "lynx", "jaguar", "coleco", "gg", "sms", "md", "s32x", "tg16", "switch", "psx", "psp", "gc", "wii", "x360", "saturn", "nds", "segacd", "3do", "c64", "pet", "vic20", "c128", "plus4", "amiga", "3ds", "tgcd", "dos", "ngp", "ngpc", "mame2003", "fbneo", "pcfx", "ws", "wsc" }));
        foreach (var id in new[] { "gb", "gbc", "gba", "n64", "nes", "fds", "snes", "satellaview", "sufami", "vb", "a2600", "a5200", "a7800", "lynx", "jaguar", "coleco", "gg", "sms", "md", "s32x", "tg16", "switch", "psx", "psp", "gc", "wii", "x360", "saturn", "nds", "segacd", "3do", "c64", "pet", "vic20", "c128", "plus4", "amiga", "3ds", "tgcd", "dos", "ngp", "ngpc", "mame2003", "fbneo", "pcfx", "ws", "wsc" })
        {
            // B30: the library folder is created by the first ROM written there.
            Assert.That(Directory.Exists(Path.Combine(factory.Settings["Paths:Roms"]!, id)), Is.False);
            Assert.That(Directory.Exists(Path.Combine(factory.Settings["Paths:Ingest"]!, id, "_removed")), Is.True);
        }
    }

    [Test]
    public void Startup_LoadsTheRomStorageSettings_OfConfigurableSystems()
    {
        using var factory = new ApiFactory();
        factory.AppSettings.GetAsync(Arg.Is<IReadOnlyCollection<string>>(n => n.Contains("storage.embedRoms.nds")), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["storage.embedRoms.nds"] = "true" });
        using var _ = factory.CreateClient();

        var nds = factory.Services.GetRequiredService<ISystemRegistry>().ById("nds")!;

        Assert.That((nds.EmbedRomsInZip, nds.DefaultEmbedRomsInZip), Is.EqualTo((true, false)));
    }
}
