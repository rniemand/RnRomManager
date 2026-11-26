namespace RnRomManager.Core.Media;

/// <summary>The suggestions, or the S8 failure title when IGDB couldn't be used.</summary>
public sealed record ImageSuggestionResult(IReadOnlyList<ImageSuggestion>? Suggestions, string? FailedTitle)
{
    public static ImageSuggestionResult Ok(IReadOnlyList<ImageSuggestion> suggestions) => new(suggestions, null);

    public static ImageSuggestionResult Failed(string title) => new(null, title);
}
