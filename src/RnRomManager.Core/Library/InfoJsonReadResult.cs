namespace RnRomManager.Core.Library;

/// <summary>Outcome of <see cref="InfoJsonReader.Read"/>: exactly one of the three shapes.</summary>
public sealed record InfoJsonReadResult
{
    public required InfoJsonReadResultOutcome Kind { get; init; }

    /// <summary>Set when <see cref="InfoJsonReadResultOutcome.Ok"/> (already upgraded to the current version).</summary>
    public InfoJsonGame? Game { get; init; }

    /// <summary>The version found in the file (before any upgrade); null when it couldn't be read.</summary>
    public int? FileVersion { get; init; }

    /// <summary>The first failing rule when <see cref="InfoJsonReadResultOutcome.Invalid"/>.</summary>
    public string? Rule { get; init; }

    public static InfoJsonReadResult Ok(InfoJsonGame game, int fileVersion) => new() { Kind = InfoJsonReadResultOutcome.Ok, Game = game, FileVersion = fileVersion };
    public static InfoJsonReadResult Invalid(string rule, int? fileVersion = null) => new() { Kind = InfoJsonReadResultOutcome.Invalid, Rule = rule, FileVersion = fileVersion };
    public static InfoJsonReadResult Newer(int fileVersion) => new() { Kind = InfoJsonReadResultOutcome.NewerSchema, FileVersion = fileVersion };
}
