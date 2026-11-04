namespace RnRomManager.Core.Ingest;

public sealed record ReviewResolutionResult(string? Code = null, Guid? GameId = null, Dictionary<string, string[]>? Errors = null)
{
    public static ReviewResolutionResult Invalid(string field, string error) =>
        new("ValidationFailed", Errors: new() { [field] = [error] });
}
