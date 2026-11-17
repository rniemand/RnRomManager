using Microsoft.Extensions.DependencyInjection;
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

        Assert.That(registry.All.Select(h => h.Id), Is.EqualTo(new[] { "gb", "gbc", "gba", "n64", "nes", "snes", "vb", "a2600", "a5200", "a7800", "lynx", "jaguar", "coleco", "gg", "sms", "md", "s32x", "tg16", "switch", "psx", "psp", "gc", "wii", "x360", "saturn", "nds", "segacd", "3do", "c64", "pet", "vic20", "amiga", "3ds" }));
        foreach (var id in new[] { "gb", "gbc", "gba", "n64", "nes", "snes", "vb", "a2600", "a5200", "a7800", "lynx", "jaguar", "coleco", "gg", "sms", "md", "s32x", "tg16", "switch", "psx", "psp", "gc", "wii", "x360", "saturn", "nds", "segacd", "3do", "c64", "pet", "vic20", "amiga", "3ds" })
        {
            // B30: the library folder is created by the first ROM written there.
            Assert.That(Directory.Exists(Path.Combine(factory.Settings["Paths:Roms"]!, id)), Is.False);
            Assert.That(Directory.Exists(Path.Combine(factory.Settings["Paths:Ingest"]!, id, "_removed")), Is.True);
        }
    }
}
