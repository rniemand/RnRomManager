using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Library;

/// <summary>A row of <c>games</c> (spec §14.1): the DB mirror of a library ZIP's <c>info.json</c>.</summary>
public sealed record GameRow
{
    public required Guid Id { get; init; }
    public required string SystemId { get; init; }
    public required string Title { get; init; }
    public required string DisplayTitle { get; init; }

    /// <summary>Relative to <c>Paths:Roms</c>, <c>/</c> separators.</summary>
    public required string ZipPath { get; init; }

    public required string Bucket { get; init; }
    public int? ReleaseYear { get; init; }
    public required IReadOnlyList<string> Developers { get; init; }
    public required IReadOnlyList<string> Publishers { get; init; }
    public required IReadOnlyList<string> Genres { get; init; }
    public string? Description { get; init; }
    public bool HasDefaultBoxArt { get; init; }
    public required long ZipSizeBytes { get; init; }

    /// <summary>Whole seconds, UTC (§12.1).</summary>
    public required DateTime ZipMtimeUtc { get; init; }

    public required int InfoSchemaVersion { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

/// <summary>A row of <c>roms</c> (spec §14.1).</summary>
public sealed record RomRow
{
    public long Id { get; init; }
    public required Guid GameId { get; init; }
    public required string SystemId { get; init; }
    public required string FileName { get; init; }
    public required string OriginalFileName { get; init; }
    public required string BaseTitle { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required IReadOnlyList<string> Flags { get; init; }

    /// <summary><c>|USA|Europe|</c>: pipe at both ends, so <c>LIKE '%|USA|%'</c> matches whole regions.</summary>
    public required string RegionKey { get; init; }

    public string? Revision { get; init; }
    public bool IsPrimary { get; init; }
    public required IdentificationMethod IdMethod { get; init; }
    public string? DatName { get; init; }
    public string? DatVersion { get; init; }
    public string? DatGameName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }

    /// <summary>The header object as JSON text; null when unreadable.</summary>
    public string? Header { get; init; }

    public required DateTime DateAdded { get; init; }
    public required DateTime DateModified { get; init; }
}

/// <summary>A row of <c>images</c> (spec §14.1).</summary>
public sealed record ImageRow
{
    public long Id { get; init; }
    public required Guid GameId { get; init; }
    public required string FileName { get; init; }
    public required ImageType Type { get; init; }
    public required string Source { get; init; }
    public string? SourceUrl { get; init; }
    public bool IsDefault { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha1 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTime DateAdded { get; init; }
}

/// <summary>What the sync compares to decide whether a ZIP must be opened (§12.1 quick mode).</summary>
public sealed record GameSyncState(Guid Id, string ZipPath, long ZipSizeBytes, DateTime ZipMtimeUtc);

/// <summary>One game to (re)write in the DB: its ZIP's <c>info.json</c> plus the file facts.</summary>
/// <param name="ZipPath">Relative to <c>Paths:Roms</c>, <c>/</c> separators.</param>
public sealed record MirrorEntry(InfoJsonGame Info, string ZipPath, long ZipSizeBytes, DateTime ZipMtimeUtc)
{
    /// <summary>The <c>games</c>/<c>roms</c>/<c>images</c> rows for this entry (§14.1 derived columns included).</summary>
    public (GameRow Game, IReadOnlyList<RomRow> Roms, IReadOnlyList<ImageRow> Images) ToRows()
    {
        var game = new GameRow
        {
            Id = Info.GameId,
            SystemId = Info.System,
            Title = Info.Title,
            DisplayTitle = Info.DisplayTitle,
            ZipPath = ZipPath,
            Bucket = Bucket.For(ZipPath[(ZipPath.LastIndexOf('/') + 1)..]),
            ReleaseYear = Info.ReleaseYear,
            Developers = Info.Developers,
            Publishers = Info.Publishers,
            Genres = Info.Genres,
            Description = Info.Description,
            HasDefaultBoxArt = Info.Images.Any(i => i.Type == ImageType.BoxArt && i.IsDefault),
            ZipSizeBytes = ZipSizeBytes,
            ZipMtimeUtc = WholeSeconds(ZipMtimeUtc),
            InfoSchemaVersion = Info.SchemaVersion,
            CreatedAt = Info.Metadata.CreatedAt.UtcDateTime,
            UpdatedAt = Info.Metadata.UpdatedAt.UtcDateTime,
        };

        var roms = Info.Roms.Select(r => new RomRow
        {
            GameId = Info.GameId,
            SystemId = Info.System,
            FileName = r.FileName,
            OriginalFileName = r.OriginalFileName,
            BaseTitle = r.BaseTitle,
            Regions = r.Regions,
            Languages = r.Languages,
            Flags = r.Flags,
            RegionKey = "|" + string.Concat(r.Regions.Select(region => region + "|")),
            Revision = r.Revision,
            IsPrimary = r.IsPrimary,
            IdMethod = r.Identification.Method,
            DatName = r.Identification.DatName,
            DatVersion = r.Identification.DatVersion,
            DatGameName = r.Identification.DatGameName,
            SizeBytes = r.Metadata.SizeBytes,
            Crc32 = r.Metadata.Crc32,
            Md5 = r.Metadata.Md5,
            Sha1 = r.Metadata.Sha1,
            Header = r.Header?.ToJsonString(),
            DateAdded = r.Metadata.DateAdded.UtcDateTime,
            DateModified = r.Metadata.DateModified.UtcDateTime,
        }).ToList();

        var images = Info.Images.Select(i => new ImageRow
        {
            GameId = Info.GameId,
            FileName = i.FileName,
            Type = i.Type,
            Source = i.Source,
            SourceUrl = i.SourceUrl,
            IsDefault = i.IsDefault,
            SizeBytes = i.SizeBytes,
            Sha1 = i.Sha1,
            Width = i.Width,
            Height = i.Height,
            DateAdded = i.DateAdded.UtcDateTime,
        }).ToList();

        return (game, roms, images);
    }

    /// <summary><c>zip_mtime_utc</c> is compared in whole seconds (§12.1): FS/SMB precision differs.</summary>
    public static DateTime WholeSeconds(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
