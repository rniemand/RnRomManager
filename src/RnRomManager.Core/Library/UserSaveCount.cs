namespace RnRomManager.Core.Library;

/// <summary>S7, SV3: a user's own saves of one game, and how many of them are save states (null for a row mirrored before
/// <c>state_count</c> existed, until the game's next write).</summary>
public sealed record UserSaveCount(int Saves, int? States);
