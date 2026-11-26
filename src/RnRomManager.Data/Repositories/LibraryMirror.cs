using System.Data;
using System.IO.Compression;
using Dapper;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Users;

namespace RnRomManager.Data.Repositories;

public sealed class LibraryMirror : ILibraryMirror
{
    private readonly IDbConnectionFactory _connections;
    private readonly string? _roms;

    /// <param name="paths">Where the ZIPs are, to count their saves; without it every game mirrors 0 saves.</param>
    public LibraryMirror(IDbConnectionFactory connections, IOptions<PathsOptions>? paths = null)
    {
        JsonStringListHandler.Register();
        _connections = connections;
        _roms = paths?.Value.Roms;
    }

    public async Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken)
    {
        var counts = replace.Select(e => e.ToRows()).Select(r => (Rows: r, Counts: SaveCounts(r.Game.ZipPath))).ToList();
        var rows = counts.Select(c => c.Rows with { Game = c.Rows.Game with { SaveCount = c.Counts.Total } }).ToList();
        var ownerCounts = counts.ToDictionary(c => c.Rows.Game.Id, c => c.Counts.PerOwner);
        var ids = delete.Concat(rows.Select(r => r.Game.Id)).Distinct().ToList();
        var zipPaths = rows.Select(r => r.Game.ZipPath).ToList();

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // A rewritten ZIP keeps its releases' ids (matched by the unique SHA1), so a client holding a rom id stays valid.
        var romIds = (await connection.QueryAsync<(long Id, string Sha1)>(new CommandDefinition("""
            SELECT r.id, r.sha1 FROM roms r JOIN games g ON g.id = r.game_id WHERE g.id IN @ids OR g.zip_path IN @zipPaths
            """, new { ids, zipPaths }, transaction, cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Sha1, r => r.Id, StringComparer.OrdinalIgnoreCase);

        // Cascades to roms/rom_files/images. Deleting first lets moves and swapped paths re-insert without unique clashes.
        if (ids.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM games WHERE id IN @ids",
                new { ids }, transaction, cancellationToken: cancellationToken));
        if (zipPaths.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM games WHERE zip_path IN @zipPaths",
                new { zipPaths }, transaction, cancellationToken: cancellationToken));

        foreach (var (game, roms, images, files) in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO games (id, system_id, title, display_title, zip_path, bucket, release_year, developers, publishers,
                                   genres, description, has_default_boxart, rating, favourite, igdb_id, save_count, zip_size_bytes, zip_mtime_utc, info_schema_version,
                                   created_at, updated_at)
                VALUES (@Id, @SystemId, @Title, @DisplayTitle, @ZipPath, @Bucket, @ReleaseYear, @Developers, @Publishers,
                        @Genres, @Description, @HasDefaultBoxArt, @Rating, @Favourite, @IgdbId, @SaveCount, @ZipSizeBytes, @ZipMtimeUtc, @InfoSchemaVersion,
                        @CreatedAt, @UpdatedAt)
                """, game, transaction, cancellationToken: cancellationToken));

            // S6: the delete above cascaded the game's old per-owner rows.
            if (ownerCounts[game.Id].Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO game_user_save_counts (game_id, username, save_count) VALUES (@GameId, @Username, @SaveCount)
                    """, ownerCounts[game.Id].Select(o => new { GameId = game.Id, Username = o.Key, SaveCount = o.Value }),
                    transaction, cancellationToken: cancellationToken));

            if (roms.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO roms (id, game_id, system_id, file_name, original_file_name, base_title, regions, languages, flags,
                                      region_key, revision, is_primary, id_method, dat_name, dat_version, dat_game_name,
                                      size_bytes, crc32, md5, sha1, header, date_added, date_modified, external_file_name, version)
                    VALUES (@Id, @GameId, @SystemId, @FileName, @OriginalFileName, @BaseTitle, @Regions, @Languages, @Flags,
                            @RegionKey, @Revision, @IsPrimary, @IdMethod, @DatName, @DatVersion, @DatGameName,
                            @SizeBytes, @Crc32, @Md5, @Sha1, @Header, @DateAdded, @DateModified, @ExternalFileName, @Version)
                    """, roms.Select(r => new
                    {
                        Id = romIds.TryGetValue(r.Sha1, out var id) ? id : (long?)null, // null: AUTO_INCREMENT picks a new one
                        r.GameId, r.SystemId, r.FileName, r.OriginalFileName, r.BaseTitle, r.Regions, r.Languages, r.Flags,
                        r.RegionKey, r.Revision, r.IsPrimary, IdMethod = r.IdMethod.ToString(), r.DatName, r.DatVersion,
                        r.DatGameName, r.SizeBytes, r.Crc32, r.Md5, r.Sha1, r.Header, r.DateAdded, r.DateModified, r.ExternalFileName, r.Version,
                    }), transaction, cancellationToken: cancellationToken));

            // B12: roms are batch-inserted without reading ids back, so each file finds its rom by the unique main sha1.
            if (files.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO rom_files (rom_id, game_id, ordinal, file_name, external_file_name, original_file_name,
                                           size_bytes, crc32, md5, sha1)
                    SELECT r.id, r.game_id, @Ordinal, @FileName, @ExternalFileName, @OriginalFileName, @SizeBytes, @Crc32, @Md5, @Sha1
                    FROM roms r
                    WHERE r.sha1 = @RomSha1
                    """, files, transaction, cancellationToken: cancellationToken));

            if (images.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO images (game_id, file_name, type, source, source_url, is_default, is_cover, size_bytes, sha1, width, height, date_added)
                    VALUES (@GameId, @FileName, @Type, @Source, @SourceUrl, @IsDefault, @IsCover, @SizeBytes, @Sha1, @Width, @Height, @DateAdded)
                    """, images.Select(i => new
                    {
                        i.GameId, i.FileName, Type = i.Type.ToString(), i.Source, i.SourceUrl, i.IsDefault, i.IsCover, i.SizeBytes, i.Sha1,
                        i.Width, i.Height, i.DateAdded,
                    }), transaction, cancellationToken: cancellationToken));
        }

        // A4: game_actions has no FK, so the cover-pulled time is upserted here and the IGDB columns are never touched.
        foreach (var entry in replace)
        {
            var pulled = InfoJsonActions.Parse(entry.Info.Actions?.CoverImagePulled)?.UtcDateTime;
            await connection.ExecuteAsync(new CommandDefinition(pulled is null
                    ? "UPDATE game_actions SET cover_image_pulled_at = NULL WHERE game_id = @Id"
                    : """
                      INSERT INTO game_actions (game_id, cover_image_pulled_at) VALUES (@Id, @Pulled)
                      ON DUPLICATE KEY UPDATE cover_image_pulled_at = VALUES(cover_image_pulled_at)
                      """,
                new { Id = entry.Info.GameId, Pulled = pulled }, transaction, cancellationToken: cancellationToken));
        }
        var gone = delete.Except(rows.Select(r => r.Game.Id)).ToList();
        if (gone.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM game_actions WHERE game_id IN @gone",
                new { gone }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, int> NoOwners = new Dictionary<string, int>();

    /// <summary>
    /// The ZIP's <c>saves/</c> entries but the index (<c>games.save_count</c>), and per owner (S6) the saves of a readable
    /// <c>saves.json</c> whose <c>uploadedBy</c> passes <see cref="UsernamePolicy"/>, grouped ignoring case like the column's
    /// collation. 0 and none when the ZIP can't be read (display only: the next write or sync corrects it).
    /// </summary>
    private (int Total, IReadOnlyDictionary<string, int> PerOwner) SaveCounts(string zipPath)
    {
        if (_roms is null)
            return (0, NoOwners);
        try
        {
            using var zip = ZipFile.OpenRead(SafePath.Resolve(_roms, zipPath));
            var total = zip.Entries.Count(e => e.FullName.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal)
                && e.FullName != LibraryZip.SavesIndexEntry && !e.FullName.EndsWith('/'));
            return (total, OwnerCounts(zip));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or UnsafePathException)
        {
            return (0, NoOwners);
        }
    }

    private static IReadOnlyDictionary<string, int> OwnerCounts(ZipArchive zip)
    {
        if (zip.GetEntry(LibraryZip.SavesIndexEntry) is not { } index || index.Length > GameSaves.MaxIndexBytes)
            return NoOwners;
        using var copy = new MemoryStream();
        using (var stream = index.Open())
        {
            // Bounded even if the entry's header lies: going past the limit makes Parse call it unreadable.
            var buffer = new byte[81920];
            int read;
            while (copy.Length <= GameSaves.MaxIndexBytes && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                copy.Write(buffer, 0, read);
        }
        var result = GameSaves.Parse(copy.ToArray(), name => zip.GetEntry(name)?.Length);
        if (result.State != SavesState.Valid)
            return NoOwners;
        return result.Index!.Saves
            .Where(s => UsernamePolicy.Validate(s.UploadedBy) is null)
            .GroupBy(s => s.UploadedBy!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }
}
