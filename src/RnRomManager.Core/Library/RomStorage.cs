using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>S3a: a game's effective ROM storage, where its info.json is at hand.</summary>
public static class RomStorage
{
    /// <returns>
    /// true when the handler embeds ROMs, or when it's <see cref="ISystemHandler.LegacyEmbeddedRoms"/> and every ROM of
    /// the game has no <c>externalFileName</c> (a legacy game still waiting for the storage migration).
    /// </returns>
    public static bool IsEmbedded(ISystemHandler handler, InfoJsonGame game) =>
        handler.EmbedRomsInZip || (handler.LegacyEmbeddedRoms && game.Roms.All(r => r.ExternalFileName is null));

    /// <summary>S3: a legacy game of a <see cref="ISystemHandler.LegacyEmbeddedRoms"/> system, refused by every write.</summary>
    public static bool IsLegacy(ISystemHandler handler, InfoJsonGame game) =>
        !handler.EmbedRomsInZip && IsEmbedded(handler, game);
}
