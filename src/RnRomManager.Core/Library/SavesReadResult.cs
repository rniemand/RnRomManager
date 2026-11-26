namespace RnRomManager.Core.Library;

/// <param name="Index">Set for <see cref="SavesState.Valid"/>.</param>
/// <param name="Problem">Why it is <see cref="SavesState.Unreadable"/>.</param>
public sealed record SavesReadResult(SavesState State, GameSavesIndex? Index = null, string? Problem = null);
