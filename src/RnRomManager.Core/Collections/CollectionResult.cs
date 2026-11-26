namespace RnRomManager.Core.Collections;

/// <summary>Outcome of a <see cref="CollectionService"/> call; the API maps each failure to its status (B3).</summary>
public sealed record CollectionResult
{
    /// <summary>The collection after the call (null after a delete or a membership change).</summary>
    public Collection? Collection { get; private init; }

    /// <summary>Games newly added (B13).</summary>
    public int Added { get; private init; }

    public CollectionResultFailure Error { get; private init; }
    public Dictionary<string, string[]>? Errors { get; private init; }

    public static CollectionResult Ok(Collection? collection = null, int added = 0) => new() { Collection = collection, Added = added };
    public static CollectionResult Fail(CollectionResultFailure error) => new() { Error = error };
    public static CollectionResult Invalid(string field, string message) =>
        new() { Error = CollectionResultFailure.ValidationFailed, Errors = new() { [field] = [message] } };

    public static CollectionResult NotFound { get; } = Fail(CollectionResultFailure.NotFound);
}
