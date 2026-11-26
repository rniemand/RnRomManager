namespace RnRomManager.Core.Library;

public enum InfoJsonReadResultOutcome
{
    Ok,

    /// <summary>Fails a §6.2 rule: <c>InfoJsonInvalid</c>.</summary>
    Invalid,

    /// <summary><c>schemaVersion</c> &gt; current: read-only, <c>InfoJsonNewerSchema</c>.</summary>
    NewerSchema,
}
