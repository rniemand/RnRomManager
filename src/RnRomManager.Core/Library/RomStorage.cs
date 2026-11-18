using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>S3a: a game's effective ROM storage, where its info.json is at hand.</summary>
public static class RomStorage
{
    /// <returns>
    /// For a <see cref="ISystemHandler.LegacyEmbeddedRoms"/> handler, the game's own layout (B4): true when every ROM has
    /// no <c>externalFileName</c>. Otherwise the handler's <see cref="ISystemHandler.EmbedRomsInZip"/>.
    /// </returns>
    public static bool IsEmbedded(ISystemHandler handler, InfoJsonGame game) =>
        handler.LegacyEmbeddedRoms ? game.Roms.All(r => r.ExternalFileName is null) : handler.EmbedRomsInZip;

    /// <summary>S3, B4: a game whose layout differs from its system's effective storage, waiting for the storage job; refused by every write.</summary>
    public static bool IsPending(ISystemHandler handler, InfoJsonGame game) =>
        handler.LegacyEmbeddedRoms && IsEmbedded(handler, game) != handler.EmbedRomsInZip;
}
