using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Data.Repositories;

public sealed class JobItemRepository(IDbConnectionFactory connections) : IJobItemRepository
{
    public async Task<long> InsertAsync(JobItem item, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO job_items (job_id, file_id, system_id, source_path, archive_entry, outcome, reason, details, game_id, sha1, created_at)
            VALUES (@JobId, @FileId, @SystemId, @SourcePath, @ArchiveEntry, @Outcome, @Reason, @Details, @GameId, @Sha1, @CreatedAt);
            SELECT LAST_INSERT_ID();
            """;
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, item, cancellationToken: cancellationToken));
    }

    public async Task<Page<JobItem>> ListAsync(
        Guid jobId,
        string? outcome,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        const string where = "WHERE job_id = @jobId AND (@outcome IS NULL OR outcome = @outcome)";
        var args = new { jobId, outcome, offset = Page<JobItem>.Offset(page, pageSize), pageSize };
        await using var connection = connections.Create();
        return await connection.QueryPageAsync<JobItem>($"SELECT COUNT(*) FROM job_items {where}", $"""
            SELECT id AS Id, job_id AS JobId, file_id AS FileId, system_id AS SystemId, source_path AS SourcePath,
                   archive_entry AS ArchiveEntry, outcome AS Outcome, reason AS Reason, details AS Details,
                   game_id AS GameId, sha1 AS Sha1, created_at AS CreatedAt
            FROM job_items {where} ORDER BY id LIMIT @pageSize OFFSET @offset
            """, args, page, pageSize, cancellationToken);
    }
}
