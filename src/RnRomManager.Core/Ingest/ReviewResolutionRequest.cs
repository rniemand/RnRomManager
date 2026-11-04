namespace RnRomManager.Core.Ingest;

public sealed record ReviewResolutionRequest
{
    public string? Action { get; init; }
    public string? DatSystem { get; init; }
    public string? DatGameName { get; init; }
    public Guid? TargetGameId { get; init; }
    public string? Flag { get; init; }
    public string? Title { get; init; }
    public string[]? Regions { get; init; }
    public string[]? Languages { get; init; }
    public string? Revision { get; init; }
    public string[]? Flags { get; init; }
}
