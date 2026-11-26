namespace RnRomManager.Api.Endpoints;

/// <summary>The running total of opened lengths against <see cref="PlayDisc.MaxBytes"/>, and the 409 title once it's passed.</summary>
internal sealed class Budget(string tooLargeTitle)
{
    public long Total { get; set; }

    public string TooLargeTitle { get; } = tooLargeTitle;
}
