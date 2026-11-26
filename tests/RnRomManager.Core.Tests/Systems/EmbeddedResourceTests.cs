using RnRomManager.Core.Systems;
using RnRomManager.Systems.Snes;

namespace RnRomManager.Core.Tests.Systems;

/// <summary>D1: one loader for every handler assembly's <c>Resources</c> folder.</summary>
public class EmbeddedResourceTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Test]
    public void Load_ReadsTheResourceFromTheGivenAssembly()
    {
        var bytes = EmbeddedResource.Load(typeof(SnesHandler).Assembly, "boxart.snes.png");

        Assert.That(bytes[..8], Is.EqualTo(PngSignature));
    }

    [Test]
    public void Load_MissingResource_Throws() =>
        Assert.That(() => EmbeddedResource.Load(typeof(SnesHandler).Assembly, "nope.png"),
            Throws.InvalidOperationException.With.Message.EqualTo("Embedded resource nope.png is missing."));
}
