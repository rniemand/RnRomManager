namespace RnRomManager.Core.Media;

internal sealed record IgdbGame(string Name, IgdbImage? Cover, IReadOnlyList<IgdbImage> Screenshots);
