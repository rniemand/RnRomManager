using RnRomManager.Core.Systems;
using RnRomManager.Systems.Snes;

namespace RnRomManager.Core.Tests.Systems;

/// <summary>D1: a handler's box art loads from its own assembly, on first use only.</summary>
public class BoxArtTests
{
    [Test]
    public void Lazy_LoadsFromTheOwnersAssembly()
    {
        var art = BoxArt.Lazy(typeof(SnesHandler), "boxart.snes.png");

        Assert.That(art.IsValueCreated, Is.False);
        Assert.That(art.Value.ToArray(), Is.EqualTo(EmbeddedResource.Load(typeof(SnesHandler).Assembly, "boxart.snes.png")));
        Assert.That(new SnesHandler().DefaultBoxArtPng.ToArray(), Is.EqualTo(art.Value.ToArray()));
    }

    [Test]
    public void Lazy_MissingResource_ThrowsOnFirstUse()
    {
        var art = BoxArt.Lazy(typeof(SnesHandler), "nope.png");

        Assert.That(() => art.Value,
            Throws.InvalidOperationException.With.Message.EqualTo("Embedded resource nope.png is missing."));
    }
}
