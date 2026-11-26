namespace RnRomManager.Core.Reports;

/// <param name="Id">The game to open, or null when no other game is missing images.</param>
/// <param name="CurrentIsOnly">True when <c>Id</c> is null because the anchor itself is the only game still missing images.</param>
public sealed record MissingImageSuccessor(Guid? Id, bool CurrentIsOnly);
