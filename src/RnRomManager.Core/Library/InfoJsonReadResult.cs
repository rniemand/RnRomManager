namespace RnRomManager.Core.Library;

/// <summary>Outcome of <see cref="InfoJsonReader.Read"/>: exactly one of the three shapes.</summary>
public sealed record InfoJsonReadResult
{
    public enum Outcome
    {
        Ok,

        /// <summary>Fails a §6.2 rule: <c>InfoJsonInvalid</c>.</summary>
        Invalid,

        /// <summary><c>schemaVersion</c> &gt; current: read-only, <c>InfoJsonNewerSchema</c>.</summary>
        NewerSchema,
    }

    public required Outcome Kind { get; init; }

    /// <summary>Set when <see cref="Outcome.Ok"/> (already upgraded to the current version).</summary>
    public InfoJsonGame? Game { get; init; }

    /// <summary>The version found in the file (before any upgrade); null when it couldn't be read.</summary>
    public int? FileVersion { get; init; }

    /// <summary>The first failing rule when <see cref="Outcome.Invalid"/>.</summary>
    public string? Rule { get; init; }

    public static InfoJsonReadResult Ok(InfoJsonGame game, int fileVersion) => new() { Kind = Outcome.Ok, Game = game, FileVersion = fileVersion };
    public static InfoJsonReadResult Invalid(string rule, int? fileVersion = null) => new() { Kind = Outcome.Invalid, Rule = rule, FileVersion = fileVersion };
    public static InfoJsonReadResult Newer(int fileVersion) => new() { Kind = Outcome.NewerSchema, FileVersion = fileVersion };
}
