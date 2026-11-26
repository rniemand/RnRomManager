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

    [Test]
    public void RomStorage_WithoutOverride_IsFixedAtEmbedRomsInZip()
    {
        ISystemHandler handler = new Plain();
        Assert.That((handler.RomStorageConfigurable, handler.DefaultEmbedRomsInZip), Is.EqualTo((false, true)));
    }

    [Test]
    public void SourceExtension_WithoutOverride_IsTheLowerCasedExtension()
    {
        ISystemHandler handler = new Plain();
        Assert.That(handler.SourceExtension("Game.D64.GZ"), Is.EqualTo(".gz"));
    }
}
