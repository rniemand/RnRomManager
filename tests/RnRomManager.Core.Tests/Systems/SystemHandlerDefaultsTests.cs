using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Tests.Systems;

public class SystemHandlerDefaultsTests
{
    [Test]
    public void ShortName_WithoutOverride_IsDisplayName()
    {
        // A real class: an NSubstitute substitute intercepts default members and returns "".
        ISystemHandler handler = new Plain();
        Assert.That(handler.ShortName, Is.EqualTo("Plain System"));
    }

    private sealed class Plain : ISystemHandler
    {
        public string Id => "plain";
        public string DisplayName => "Plain System";
        public string Family => "plain";
        public IReadOnlyList<string> RomExtensions => [".bin"];
        public long MinRomSizeBytes => 1;
        public long MaxRomSizeBytes => 1024;
        public string DatName => "Plain";
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ReadOnlyMemory<byte>.Empty;
        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    }
}
