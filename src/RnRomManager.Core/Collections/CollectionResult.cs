namespace RnRomManager.Core.Collections;

/// <summary>Outcome of a <see cref="CollectionService"/> call; the API maps each failure to its status (B3).</summary>
public sealed record CollectionResult
{
    public enum Failure
    {
        None,
        NotFound,
        ValidationFailed,
        NameTaken,
    }

    /// <summary>The collection after the call (null after a delete or a membership change).</summary>
    public Collection? Collection { get; private init; }

    /// <summary>Games newly added (B13).</summary>
    public int Added { get; private init; }

    public Failure Error { get; private init; }
    public Dictionary<string, string[]>? Errors { get; private init; }

    public static CollectionResult Ok(Collection? collection = null, int added = 0) => new() { Collection = collection, Added = added };
    public static CollectionResult Fail(Failure error) => new() { Error = error };
    public static CollectionResult Invalid(string field, string message) =>
        new() { Error = Failure.ValidationFailed, Errors = new() { [field] = [message] } };

    public static CollectionResult NotFound { get; } = Fail(Failure.NotFound);
}

/// <summary>A PATCH (B4): a null <see cref="Name"/> is unchanged; <see cref="Notes"/> applies only when <see cref="NotesSet"/>.</summary>
public sealed record CollectionPatch(string? Name = null, bool NotesSet = false, string? Notes = null);
