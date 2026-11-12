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

    /// <summary>B8: a titledb DAT pick stored as an update (its base's <c>| 0x800</c> id, or the update picked).</summary>
    public bool? Update { get; init; }

    /// <summary>The <c>update</c> error for any action other than <c>Dat</c>.</summary>
    public const string UpdateNeedsDat = "Only a DAT pick can be stored as an update.";
}
