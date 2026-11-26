using RnRomManager.Core.Library;

namespace RnRomManager.Core.Media;

/// <summary>IGDB suggestions: candidate box art and screenshots for a game (S3–S6).</summary>
public interface IImageSuggestions
{
    /// <summary>S1: both <c>Sources:Igdb*</c> keys are set.</summary>
    bool Configured { get; }

    /// <summary><paramref name="type"/> is <see cref="ImageType.BoxArt"/> or <see cref="ImageType.Snap"/>. Cancellation propagates (S3a).</summary>
    Task<ImageSuggestionResult> SuggestAsync(
        string title,
        string systemId,
        ImageType type,
        CancellationToken cancellationToken);
}
