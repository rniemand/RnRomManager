namespace RnRomManager.Core.Library;

/// <summary>
/// A1: <c>info.json</c> → <c>actions</c>, what the Admin actions did to the game. Optional and left out when empty.
/// </summary>
public sealed record InfoJsonActions
{
    /// <summary>
    /// When Pull Cover Images last made the IGDB cover this game's cover: round-trip (<c>"O"</c>) UTC, whole seconds.
    /// A string, so a bad value reaches <see cref="InfoJsonValidator"/> rather than failing to deserialize.
    /// </summary>
    public string? CoverImagePulled { get; init; }

    /// <summary>A1: the value to write for <paramref name="time"/>, UTC truncated to whole seconds.</summary>
    public static string Format(DateTimeOffset time) =>
        new DateTimeOffset(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <returns>The time as UTC, or null when absent or not an ISO-8601 time.</returns>
    public static DateTimeOffset? Parse(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var time) ? time.ToUniversalTime() : null;
}
