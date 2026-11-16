using System.Text.Json;
using System.Text.Json.Serialization;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>B2: one record of <c>saves/saves.json</c>; its bytes are the entry <c>saves/&lt;id&gt;&lt;extension&gt;</c>.</summary>
public sealed record GameSave
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary><see cref="GameSaves.KindState"/> or <see cref="GameSaves.KindBattery"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The release's <c>fileName</c> in <c>info.json</c> (B4: matched ignoring case; none matching is an orphan).</summary>
    public required string Release { get; init; }

    /// <summary>B1: empty, or <c>.</c> plus 1–8 of <c>[a-z0-9]</c>.</summary>
    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }
    public required string Sha1 { get; init; }
    public required string? UploadedBy { get; init; }
    public required DateTimeOffset UploadedAt { get; init; }
    public required string? UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The ZIP entry holding the bytes.</summary>
    [JsonIgnore]
    public string Entry => LibraryZip.SaveEntry(Id + Extension);
}

/// <summary>B2: the whole <c>saves/saves.json</c>.</summary>
public sealed record GameSavesIndex
{
    public required int Version { get; init; }
    public required IReadOnlyList<GameSave> Saves { get; init; }
}

/// <summary>What a ZIP's <c>saves/</c> folder holds (B3).</summary>
public enum SavesState
{
    /// <summary>No <c>saves/</c> entry at all.</summary>
    Absent,

    /// <summary>A valid <c>saves.json</c> (B2).</summary>
    Valid,

    /// <summary><c>saves.json</c> missing while other <c>saves/</c> entries exist, unreadable, over 1 MiB or invalid.</summary>
    Unreadable,
}

/// <param name="Index">Set for <see cref="SavesState.Valid"/>.</param>
/// <param name="Problem">Why it is <see cref="SavesState.Unreadable"/>.</param>
public sealed record SavesReadResult(SavesState State, GameSavesIndex? Index = null, string? Problem = null);

/// <summary>B1, B2, B8: the <c>saves.json</c> rules, limits, reader and writer.</summary>
public static class GameSaves
{
    public const int Version = 1;
    public const int MaxIndexBytes = 1024 * 1024;
    public const long MaxSaveBytes = 256L * 1024 * 1024;
    public const int MaxPerRelease = 25;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;
    public const string KindState = "state";
    public const string KindBattery = "battery";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsKind(string? kind) => kind is KindState or KindBattery;

    /// <summary>B1: the text after the upload name's last <c>.</c>, lowercased, when it is 1–8 <c>[a-z0-9]</c>; else empty.</summary>
    public static string ExtensionOf(string? uploadFileName)
    {
        var dot = uploadFileName?.LastIndexOf('.') ?? -1;
        if (dot < 0)
            return "";
        var extension = "." + uploadFileName![(dot + 1)..].ToLowerInvariant();
        return IsExtension(extension) ? extension : "";
    }

    private static bool IsExtension(string extension) =>
        extension.Length == 0
        || (extension[0] == '.' && extension.Length is >= 2 and <= 9 && extension[1..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9'));

    /// <summary>B8: a name is trimmed, 1–100 characters, no control characters.</summary>
    /// <returns>The error, or null with <paramref name="trimmed"/> set.</returns>
    public static string? NameProblem(string? name, out string trimmed)
    {
        trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            return $"Must be 1–{MaxNameLength} characters.";
        return trimmed.Any(char.IsControl) ? "Must not contain control characters." : null;
    }

    /// <summary>B8: a description is at most 1,000 characters, with no control characters but line breaks.</summary>
    public static string? DescriptionProblem(string? description)
    {
        if (description is null)
            return null;
        if (description.Length > MaxDescriptionLength)
            return $"Must be at most {MaxDescriptionLength} characters.";
        return description.Any(c => char.IsControl(c) && c != '\n') ? "Must not contain control characters other than line breaks." : null;
    }

    /// <summary>Reads and validates a ZIP's <c>saves/</c> folder (B2, B3): the index bounded to 1 MiB, each listed entry present with its size.</summary>
    public static SavesReadResult Read(LibraryZip zip)
    {
        if (zip.EntryLength(LibraryZip.SavesIndexEntry) is null)
            return zip.EntryNames.Any(e => e.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal))
                ? new(SavesState.Unreadable, Problem: "saves.json is missing")
                : new(SavesState.Absent);
        byte[] bytes;
        try
        {
            using var stream = zip.OpenEntry(LibraryZip.SavesIndexEntry, MaxIndexBytes);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or LimitExceededException or IOException)
        {
            return new(SavesState.Unreadable, Problem: $"saves.json can't be read: {ex.Message}");
        }
        return Parse(bytes, zip.EntryLength);
    }

    /// <param name="entryLength">The length of a ZIP entry, or null when it is missing.</param>
    public static SavesReadResult Parse(byte[] bytes, Func<string, long?> entryLength)
    {
        if (bytes.Length > MaxIndexBytes)
            return new(SavesState.Unreadable, Problem: $"saves.json is over {MaxIndexBytes} bytes");
        GameSavesIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<GameSavesIndex>(bytes, Options);
        }
        catch (JsonException ex)
        {
            return new(SavesState.Unreadable, Problem: $"saves.json is not valid: {ex.Message}");
        }
        return Problem(index, entryLength) is { } problem
            ? new(SavesState.Unreadable, Problem: problem)
            : new(SavesState.Valid, index);
    }

    private static string? Problem(GameSavesIndex? index, Func<string, long?> entryLength)
    {
        if (index is null || index.Saves is null || index.Saves.Any(s => s is null))
            return "saves.json is empty or lists null";
        if (index.Version != Version)
            return $"saves.json version {index.Version} is not {Version}";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var save in index.Saves)
        {
            if (save.Id.Length != 32 || !save.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || !ids.Add(save.Id))
                return $"save id '{save.Id}' is not a unique lowercase GUID";
            if (!IsKind(save.Kind))
                return $"save {save.Id} kind '{save.Kind}' is unknown";
            if (!IsExtension(save.Extension))
                return $"save {save.Id} extension '{save.Extension}' is not valid";
            if (NameProblem(save.Name, out var trimmed) is not null || trimmed != save.Name || DescriptionProblem(save.Description) is not null)
                return $"save {save.Id} name or description is not valid";
            if (save.SizeBytes < 0 || save.Sha1.Length != 40 || !save.Sha1.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return $"save {save.Id} size or sha1 is not valid";
            if (entryLength(save.Entry) != save.SizeBytes)
                return $"{save.Entry} is missing or not {save.SizeBytes} bytes";
        }
        return null;
    }

    /// <returns>The UTF-8 <c>saves.json</c>, or null when it would be over 1 MiB (<c>SavesIndexFull</c>).</returns>
    public static byte[]? Serialize(IReadOnlyList<GameSave> saves)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new GameSavesIndex { Version = Version, Saves = saves }, Options);
        return bytes.Length > MaxIndexBytes ? null : bytes;
    }

    /// <summary>B2, B4: whether a save names <paramref name="releaseFileName"/> (ignoring case).</summary>
    public static bool Names(GameSave save, string releaseFileName) =>
        string.Equals(save.Release, releaseFileName, StringComparison.OrdinalIgnoreCase);
}
