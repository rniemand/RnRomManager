namespace RnRomManager.Core.Ingest;

public sealed record ReviewResolutionResult(string? Code = null, Guid? GameId = null, Dictionary<string, string[]>? Errors = null)
{
    /// <summary>B2: the review rows a job's resolution acted on, as read under the lock; empty for Dismiss and Delete.</summary>
    public IReadOnlyList<ReviewItem> Items { get; init; } = [];

    public static ReviewResolutionResult Invalid(string field, string error) =>
        new("ValidationFailed", Errors: new() { [field] = [error] });
}
