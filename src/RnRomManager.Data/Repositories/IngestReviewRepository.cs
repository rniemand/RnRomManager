using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Data.Repositories;

/// <summary><c>ingest_review_items</c> (spec §14.1). Grows with the review steps.</summary>
public sealed class IngestReviewRepository(IDbConnectionFactory connections) : IIngestReviewRepository
{
    public async Task<ReviewItem?> GetBySha1Async(string sha1, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<ReviewItem>(new CommandDefinition("""
            SELECT id AS Id, system_id AS SystemId, reason AS Reason, source_path AS SourcePath,
                archive_path AS ArchivePath, archive_entry AS ArchiveEntry, original_file_name AS OriginalFileName,
                size_bytes AS SizeBytes, crc32 AS Crc32, md5 AS Md5, sha1 AS Sha1, header AS Header,
                suggestions AS Suggestions, dat_identification AS DatIdentification, candidate_game_ids AS CandidateGameIds,
                status AS Status, game_id AS GameId, created_at AS CreatedAt, last_seen_job_id AS LastSeenJobId,
                resolved_at AS ResolvedAt, resolved_by AS ResolvedBy
            FROM ingest_review_items WHERE sha1 = @sha1
            """, new { sha1 }, cancellationToken: cancellationToken));
    }

    public async Task UpsertAsync(ReviewItem item, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ingest_review_items
                (system_id, reason, source_path, archive_path, archive_entry, original_file_name, size_bytes,
                 crc32, md5, sha1, header, suggestions, dat_identification, candidate_game_ids, status,
                 game_id, created_at, last_seen_job_id, resolved_at, resolved_by)
            VALUES (@SystemId, @Reason, @SourcePath, @ArchivePath, @ArchiveEntry, @OriginalFileName, @SizeBytes,
                 @Crc32, @Md5, @Sha1, @Header, @Suggestions, @DatIdentification, @CandidateGameIds, 'Pending',
                 NULL, @CreatedAt, @LastSeenJobId, NULL, NULL)
            ON DUPLICATE KEY UPDATE system_id = VALUES(system_id), reason = VALUES(reason),
                source_path = VALUES(source_path), archive_path = VALUES(archive_path), archive_entry = VALUES(archive_entry),
                original_file_name = VALUES(original_file_name), size_bytes = VALUES(size_bytes), crc32 = VALUES(crc32), md5 = VALUES(md5),
                header = VALUES(header), suggestions = VALUES(suggestions), dat_identification = VALUES(dat_identification),
                candidate_game_ids = VALUES(candidate_game_ids), status = 'Pending', game_id = NULL,
                last_seen_job_id = VALUES(last_seen_job_id), resolved_at = NULL, resolved_by = NULL
            """, item, cancellationToken: cancellationToken));
    }

    public async Task CompleteSourceAsync(string sourcePath, string? archiveEntry, IngestItemResult result, Guid jobId, DateTime now, CancellationToken cancellationToken)
    {
        if (result.Outcome is IngestOutcome.Review or IngestOutcome.Skipped)
            return;
        await using var connection = connections.Create();
        var sql = result.Outcome == IngestOutcome.Ingested
            ? "UPDATE ingest_review_items SET status = 'Resolved', game_id = @GameId, resolved_at = @now, resolved_by = NULL, last_seen_job_id = @jobId"
            : "DELETE FROM ingest_review_items";
        await connection.ExecuteAsync(new CommandDefinition(
            sql + " WHERE status = 'Pending' AND BINARY source_path = @sourcePath AND BINARY archive_entry <=> @archiveEntry",
            new { sourcePath, archiveEntry, result.GameId, now, jobId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PendingReviewSource>> ListPendingSourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<PendingReviewSource>(new CommandDefinition(
            "SELECT id AS Id, source_path AS SourcePath, archive_path AS ArchivePath FROM ingest_review_items WHERE status = 'Pending'",
            cancellationToken: cancellationToken))).AsList();
    }

    public async Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ingest_review_items WHERE id IN @ids", new { ids }, cancellationToken: cancellationToken));
    }
}
