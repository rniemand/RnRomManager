using NSubstitute;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>NSubstitute system handlers for tests (D5); callers add any other <c>.Returns(...)</c> themselves.</summary>
public static class TestHandlers
{
    /// <param name="embedRomsInZip">False is also NSubstitute's default, so <c>Fake(id)</c> matches an <c>Id</c>-only set-up.</param>
    public static ISystemHandler Fake(string id, bool embedRomsInZip = false)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns(id);
        handler.EmbedRomsInZip.Returns(embedRomsInZip);
        return handler;
    }
}
