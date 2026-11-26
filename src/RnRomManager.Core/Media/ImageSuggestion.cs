namespace RnRomManager.Core.Media;

/// <summary>IGDB suggestions S6: one candidate image. <see cref="Width"/>/<see cref="Height"/> are the stored (t_1080p) size.</summary>
public sealed record ImageSuggestion(
    string PreviewUrl,
    string ImageUrl,
    string GameName,
    int? Width,
    int? Height);
