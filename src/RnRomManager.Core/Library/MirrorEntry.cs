using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Library;

/// <summary>One game to (re)write in the DB: its ZIP's <c>info.json</c> plus the file facts.</summary>
/// <param name="ZipPath">Relative to <c>Paths:Roms</c>, <c>/</c> separators.</param>
public sealed record MirrorEntry(
    InfoJsonGame Info,
    string ZipPath,
    long ZipSizeBytes,
    DateTime ZipMtimeUtc)
{
    /// <summary>The <c>games</c>/<c>roms</c>/<c>images</c>/<c>rom_files</c> rows for this entry (§14.1 derived columns included).</summary>
    public (GameRow Game, IReadOnlyList<RomRow> Roms, IReadOnlyList<ImageRow> Images, IReadOnlyList<RomFileRow> Files) ToRows()
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
            // v3: "the cover is the placeholder" (I8).
            HasDefaultBoxArt = Info.Images.Any(i => i.IsCover && i.IsDefault),
            Rating = Info.Rating,
            Favourite = Info.Favourite,
            IgdbId = Info.IgdbId,
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
            ExternalFileName = r.ExternalFileName,
            Version = r.Version,
        }).ToList();

        var images = Info.Images.Select(i => new ImageRow
        {
            GameId = Info.GameId,
            FileName = i.FileName,
            Type = i.Type,
            Source = i.Source,
            SourceUrl = i.SourceUrl,
            IsDefault = i.IsDefault,
            IsCover = i.IsCover,
            SizeBytes = i.SizeBytes,
            Sha1 = i.Sha1,
            Width = i.Width,
            Height = i.Height,
            DateAdded = i.DateAdded.UtcDateTime,
        }).ToList();

        var files = Info.Roms.SelectMany(r => (r.Files ?? []).Select((f, ordinal) => new RomFileRow
        {
            GameId = Info.GameId,
            RomSha1 = r.Metadata.Sha1,
            Ordinal = ordinal,
            FileName = f.FileName,
            ExternalFileName = f.ExternalFileName,
            OriginalFileName = f.OriginalFileName,
            SizeBytes = f.SizeBytes,
            Crc32 = f.Crc32,
            Md5 = f.Md5,
            Sha1 = f.Sha1,
        })).ToList();

        return (game, roms, images, files);
    }

    /// <summary><c>zip_mtime_utc</c> is compared in whole seconds (§12.1): FS/SMB precision differs.</summary>
    public static DateTime WholeSeconds(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
