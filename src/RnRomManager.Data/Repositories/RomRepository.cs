using System.Data;
using Dapper;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Data.Repositories;

public sealed class RomRepository : IRomRepository
{
    private const string Select = """
        SELECT id AS Id, game_id AS GameId, system_id AS SystemId, file_name AS FileName,
               original_file_name AS OriginalFileName, base_title AS BaseTitle, regions AS Regions,
               languages AS Languages, flags AS Flags, region_key AS RegionKey, revision AS Revision,
               is_primary AS IsPrimary, id_method AS IdMethod, dat_name AS DatName, dat_version AS DatVersion,
               dat_game_name AS DatGameName, size_bytes AS SizeBytes, crc32 AS Crc32, md5 AS Md5, sha1 AS Sha1,
               header AS Header, date_added AS DateAdded, date_modified AS DateModified,
               external_file_name AS ExternalFileName, version AS Version
        FROM roms
        """;

    private readonly IDbConnectionFactory _connections;

    public RomRepository(IDbConnectionFactory connections)
    {
        JsonStringListHandler.Register();
        _connections = connections;
    }

    public async Task<IReadOnlyList<RomRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<RomRow>(new CommandDefinition(
            $"{Select} WHERE game_id = @gameId ORDER BY id", new { gameId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<RomRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<RomRow>(new CommandDefinition(
            $"{Select} WHERE id = @id AND game_id = @gameId", new { gameId, id }, cancellationToken: cancellationToken));
    }

    public async Task<RomRow?> GetBySha1Async(string sha1, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<RomRow>(
            new CommandDefinition($"{Select} WHERE sha1 = @sha1", new { sha1 }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ListGameIdsByBaseTitleAsync(string systemId, string baseTitle, CancellationToken cancellationToken)
    {
        // utf8mb4_unicode_ci: the comparison is case-insensitive.
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT game_id FROM roms WHERE system_id = @systemId AND base_title = @baseTitle",
            new { systemId, baseTitle }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<RomRow>> ListBySetNamesAsync(string systemId, IReadOnlyCollection<string> setNames, CancellationToken cancellationToken)
    {
        if (setNames.Count == 0)
            return [];
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<RomRow>(new CommandDefinition(
            $"""
            {Select}
            WHERE system_id = @systemId AND LOWER(JSON_UNQUOTE(JSON_EXTRACT(header, '$.setName'))) IN @names
            ORDER BY is_primary DESC, id
            """,
            new { systemId, names = setNames.Select(n => n.ToLowerInvariant()).ToArray() }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<(Guid GameId, string Sha1)>> ListSha1sAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<(Guid, string)>(
            new CommandDefinition("SELECT game_id, sha1 FROM roms", cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<ExternalRomRow>> ListExternalAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        // B13: main files and set files together; a main file (ordinal -1) comes before its files.
        return (await connection.QueryAsync<ExternalRomRow>(new CommandDefinition("""
            SELECT GameId, ZipPath, ExternalFileName, SizeBytes, Sha1, FileId
            FROM (
                SELECT r.game_id AS GameId, g.zip_path AS ZipPath, r.external_file_name AS ExternalFileName,
                       r.size_bytes AS SizeBytes, r.sha1 AS Sha1, CAST(NULL AS SIGNED) AS FileId, r.id AS RomId, -1 AS Ordinal
                FROM roms r
                JOIN games g ON g.id = r.game_id
                WHERE r.external_file_name IS NOT NULL
                UNION ALL
                SELECT f.game_id, g.zip_path, f.external_file_name, f.size_bytes, f.sha1, f.id, f.rom_id, f.ordinal
                FROM rom_files f
                JOIN games g ON g.id = f.game_id
            ) claims
            ORDER BY ZipPath, RomId, Ordinal
            """, cancellationToken: cancellationToken))).AsList();
    }

    private const string SelectFiles = """
        SELECT f.id AS Id, f.rom_id AS RomId, f.game_id AS GameId, r.sha1 AS RomSha1, f.ordinal AS Ordinal,
               f.file_name AS FileName, f.external_file_name AS ExternalFileName, f.original_file_name AS OriginalFileName,
               f.size_bytes AS SizeBytes, f.crc32 AS Crc32, f.md5 AS Md5, f.sha1 AS Sha1
        FROM rom_files f
        JOIN roms r ON r.id = f.rom_id
        """;

    public async Task<IReadOnlyList<RomFileRow>> ListFilesByGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<RomFileRow>(new CommandDefinition(
            $"{SelectFiles} WHERE f.game_id = @gameId ORDER BY f.rom_id, f.ordinal", new { gameId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<RomFileRow?> GetFileAsync(
        Guid gameId,
        long romId,
        long fileId,
        CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return await connection.QuerySingleOrDefaultAsync<RomFileRow>(new CommandDefinition(
            $"{SelectFiles} WHERE f.id = @fileId AND f.rom_id = @romId AND f.game_id = @gameId", new { gameId, romId, fileId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<RomFileRow>> ListFilesBySha1Async(string sha1, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        return (await connection.QueryAsync<RomFileRow>(new CommandDefinition(
            $"{SelectFiles} WHERE f.sha1 = @sha1 ORDER BY f.id", new { sha1 }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<BundleFileTarget?>> ResolveBundleFilesAsync(IReadOnlyList<BundleFileIdentity> identities,
        CancellationToken cancellationToken)
    {
        if (identities.Count == 0) return [];
        var releaseShas = identities.Select(i => i.ReleaseSha1).Distinct(StringComparer.Ordinal).ToList();

        // Two bounded reads in one snapshot: the releases by their unique SHA-1, then the set files of those releases.
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var releases = (await connection.QueryAsync<BundleRelease>(new CommandDefinition("""
            SELECT r.id AS Id, r.game_id AS GameId, r.sha1 AS Sha1, r.file_name AS FileName, g.display_title AS GameTitle
            FROM roms r JOIN games g ON g.id = r.game_id
            WHERE r.sha1 IN @releaseShas
            """, new { releaseShas }, transaction, cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Sha1, StringComparer.Ordinal);

        var memberRomIds = identities
            .Where(i => i.Member is not null && releases.TryGetValue(i.ReleaseSha1, out var r) && r.GameId == i.GameId)
            .Select(i => releases[i.ReleaseSha1].Id).Distinct().ToList();
        var files = memberRomIds.Count == 0
            ? []
            : (await connection.QueryAsync<BundleSetFile>(new CommandDefinition("""
                SELECT id AS Id, rom_id AS RomId, sha1 AS Sha1, file_name AS FileName FROM rom_files WHERE rom_id IN @memberRomIds
                """, new { memberRomIds }, transaction, cancellationToken: cancellationToken))).AsList();
        await transaction.CommitAsync(cancellationToken);
        var filesByRom = files.ToLookup(f => f.RomId);

        return identities.Select(identity =>
        {
            // B3: never substitute another game's release.
            if (!releases.TryGetValue(identity.ReleaseSha1, out var release) || release.GameId != identity.GameId) return null;
            if (identity.Member is not { } member)
                return new BundleFileTarget(release.GameId, release.GameTitle, release.Id, null, release.FileName);
            var matches = filesByRom[release.Id]
                .Where(f => string.Equals(f.Sha1, member.Sha1, StringComparison.Ordinal)
                            && string.Equals(f.FileName, member.FileName, StringComparison.Ordinal))
                .Take(2).ToList();
            return matches.Count == 1
                ? new BundleFileTarget(release.GameId, release.GameTitle, release.Id, matches[0].Id, matches[0].FileName)
                : (BundleFileTarget?)null;
        }).ToList();
    }
}
