using System.Text;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Collections;

/// <summary>
/// Collection rules (B2–B5, B4a, B8, B10–B11, B13–B14). Every call is scoped to <c>userId</c>: another user's
/// collection is <see cref="CollectionResult.Failure.NotFound"/>. Never takes the job lock (B12).
/// </summary>
public sealed class CollectionService(ICollectionRepository repository, CollectionCovers covers, TimeProvider time)
{
    public const int MaxNameLength = 100;
    public const int MaxNotesBytes = LibraryZip.MaxNoteBytes;
    public const int MaxGameIds = 500;

    public Task<Collection?> GetAsync(Guid id, int userId, CancellationToken cancellationToken) =>
        repository.GetAsync(id, userId, cancellationToken);

    public async Task<CollectionResult> CreateAsync(int userId, string? name, string? notes, CancellationToken cancellationToken)
    {
        if (ValidateName(ref name) is { } nameError)
            return CollectionResult.Invalid("name", nameError);
        if (ValidateNotes(ref notes) is { } notesError)
            return CollectionResult.Invalid("notes", notesError);

        var now = Now();
        var collection = new Collection(Guid.NewGuid(), userId, name!, notes, null, null, now, now);
        try
        {
            await repository.InsertAsync(collection, cancellationToken);
        }
        catch (CollectionNameTakenException)
        {
            return CollectionResult.Fail(CollectionResult.Failure.NameTaken);
        }

        return CollectionResult.Ok(collection);
    }

    public async Task<CollectionResult> UpdateAsync(Guid id, int userId, CollectionPatch patch, CancellationToken cancellationToken)
    {
        var name = patch.Name;
        var notes = patch.Notes;
        if (name is not null && ValidateName(ref name) is { } nameError)
            return CollectionResult.Invalid("name", nameError);
        if (patch.NotesSet && ValidateNotes(ref notes) is { } notesError)
            return CollectionResult.Invalid("notes", notesError);
        if (await repository.GetAsync(id, userId, cancellationToken) is not { } collection)
            return CollectionResult.NotFound;

        var updated = collection with { Name = name ?? collection.Name, Notes = patch.NotesSet ? notes : collection.Notes };
        if (updated == collection)
            return CollectionResult.Ok(collection); // B4a: a no-op keeps updatedAt
        updated = updated with { UpdatedAt = Now() };
        try
        {
            return await repository.UpdateAsync(updated, cancellationToken) ? CollectionResult.Ok(updated) : CollectionResult.NotFound;
        }
        catch (CollectionNameTakenException)
        {
            return CollectionResult.Fail(CollectionResult.Failure.NameTaken);
        }
    }

    /// <summary>B5: the row and its memberships, then its cover files. Games are never touched.</summary>
    public async Task<CollectionResult> DeleteAsync(Guid id, int userId, CancellationToken cancellationToken)
    {
        if (!await repository.DeleteAsync(id, userId, cancellationToken))
            return CollectionResult.NotFound;
        covers.DeleteAll(id);
        return CollectionResult.Ok();
    }

    /// <summary>B8, B10: replaces the cover with a PNG/JPEG whose dimensions can be read.</summary>
    public async Task<CollectionResult> SetCoverAsync(Guid id, int userId, byte[] bytes, CancellationToken cancellationToken)
    {
        if (ImageFormats.Detect(bytes) is null)
            return CollectionResult.Invalid("file", "The image must be a PNG or JPEG.");
        if (ImageFormats.Read(bytes) is not { } image)
            return CollectionResult.Invalid("file", "The image's dimensions can't be read.");
        if (await repository.GetAsync(id, userId, cancellationToken) is not { } collection)
            return CollectionResult.NotFound;

        var format = await covers.WriteAsync(id, image.Format, bytes, cancellationToken);
        var now = Now();
        var updated = collection with { CoverFormat = format, CoverUpdatedAt = now, UpdatedAt = now };
        bool found;
        try
        {
            found = await repository.UpdateAsync(updated, cancellationToken);
        }
        catch
        {
            DropNewFile();
            throw;
        }

        if (!found)
        {
            DropNewFile();
            return CollectionResult.NotFound;
        }

        covers.DeleteOtherFormat(id, format);
        return CollectionResult.Ok(updated);

        // B10: a same-format file is kept, since the row still points at it.
        void DropNewFile()
        {
            if (format != collection.CoverFormat)
                covers.Delete(id, format);
        }
    }

    /// <summary>B11: clears the cover columns, then deletes both possible files.</summary>
    public async Task<CollectionResult> RemoveCoverAsync(Guid id, int userId, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, userId, cancellationToken) is not { } collection)
            return CollectionResult.NotFound;
        var updated = collection;
        if (collection.CoverFormat is not null)
        {
            updated = collection with { CoverFormat = null, CoverUpdatedAt = null, UpdatedAt = Now() };
            if (!await repository.UpdateAsync(updated, cancellationToken))
                return CollectionResult.NotFound;
        }

        covers.DeleteAll(id);
        return CollectionResult.Ok(updated);
    }

    /// <summary>B13: 1–<see cref="MaxGameIds"/> ids; unknown games are skipped.</summary>
    public async Task<CollectionResult> AddGamesAsync(Guid id, int userId, IReadOnlyCollection<Guid>? gameIds, CancellationToken cancellationToken)
    {
        if (gameIds is null || gameIds.Count is 0 or > MaxGameIds)
            return CollectionResult.Invalid("gameIds", $"Give between 1 and {MaxGameIds} game ids.");
        if (await repository.GetAsync(id, userId, cancellationToken) is not { } collection)
            return CollectionResult.NotFound;

        var now = Now();
        var added = await repository.AddGamesAsync(id, gameIds.Distinct().ToList(), now, cancellationToken);
        if (added > 0)
            await repository.UpdateAsync(collection with { UpdatedAt = now }, cancellationToken);
        return CollectionResult.Ok(added: added);
    }

    /// <summary>B14: removing a non-member succeeds too.</summary>
    public async Task<CollectionResult> RemoveGameAsync(Guid id, int userId, Guid gameId, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, userId, cancellationToken) is not { } collection)
            return CollectionResult.NotFound;
        if (await repository.RemoveGameAsync(id, gameId, cancellationToken))
            await repository.UpdateAsync(collection with { UpdatedAt = Now() }, cancellationToken);
        return CollectionResult.Ok();
    }

    /// <summary>B2: trimmed, 1–100 characters (code points, as the column counts them).</summary>
    public static string? ValidateName(ref string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "A name is required.";
        return name.EnumerateRunes().Count() > MaxNameLength ? $"The name must be at most {MaxNameLength} characters." : null;
    }

    /// <summary>B2: line endings → <c>\n</c>, whitespace-only → null, at most 65,536 UTF-8 bytes (as release notes).</summary>
    public static string? ValidateNotes(ref string? notes)
    {
        notes = notes?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(notes))
        {
            notes = null;
            return null;
        }

        return Encoding.UTF8.GetByteCount(notes) > MaxNotesBytes ? $"The notes must be at most {MaxNotesBytes:N0} bytes (UTF-8)." : null;
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}
