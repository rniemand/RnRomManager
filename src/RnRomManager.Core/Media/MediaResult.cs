namespace RnRomManager.Core.Media;

/// <param name="BoxArt">Always set: the handler's default when nothing was found.</param>
public sealed record MediaResult(MediaImage BoxArt, MediaImage? Snap);
