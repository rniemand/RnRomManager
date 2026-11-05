using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Data.Repositories;

public sealed class JobRepository(IDbConnectionFactory connections) : IJobRepository
{
    private const string Select = """
        SELECT id AS Id, type AS Type, mode AS Mode, trigger_source AS `Trigger`, status AS Status,
               requested_by AS RequestedBy, started_at AS StartedAt, finished_at AS FinishedAt,
               summary AS Summary, error AS Error
        FROM jobs
        """;

    public async Task InsertAsync(Job job, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO jobs (id, type, mode, trigger_source, status, requested_by, started_at, finished_at, summary, error)
            VALUES (@Id, @Type, @Mode, @Trigger, @Status, @RequestedBy, @StartedAt, @FinishedAt, @Summary, @Error)
            """;
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            job.Id,
            Type = job.Type.ToString(),
            Mode = job.Mode?.ToString(),
            Trigger = job.Trigger.ToString(),
            Status = job.Status.ToString(),
            job.RequestedBy,
            job.StartedAt,
            job.FinishedAt,
            job.Summary,
            job.Error,
        }, cancellationToken: cancellationToken));
    }

    public async Task<int> PurgeAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM jobs WHERE started_at < @cutoff AND status <> 'Running'", new { cutoff }, cancellationToken: cancellationToken));
    }

    public async Task FinishAsync(Guid id, JobStatus status, DateTime finishedAt, string? summary, string? error, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE jobs SET status = @status, finished_at = @finishedAt, summary = @summary, error = @error WHERE id = @id";
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(sql,
            new { id, status = status.ToString(), finishedAt, summary, error }, cancellationToken: cancellationToken));
    }

    public async Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<Job>(
            new CommandDefinition($"{Select} WHERE id = @id", new { id }, cancellationToken: cancellationToken));
    }

    public async Task<Page<Job>> ListAsync(JobType? type, int page, int pageSize, CancellationToken cancellationToken)
    {
        const string where = "WHERE @type IS NULL OR type = @type";
        var args = new { type = type?.ToString(), offset = Page<Job>.Offset(page, pageSize), pageSize };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition($"SELECT COUNT(*) FROM jobs {where}", args, cancellationToken: cancellationToken));
        var items = await connection.QueryAsync<Job>(new CommandDefinition(
            $"{Select} {where} ORDER BY started_at DESC, id LIMIT @pageSize OFFSET @offset", args, cancellationToken: cancellationToken));
        return new Page<Job>(items.AsList(), page, pageSize, total);
    }

    public async Task<Job?> GetLatestAsync(JobType type, JobMode? mode, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.QueryFirstOrDefaultAsync<Job>(new CommandDefinition(
            $"{Select} WHERE type = @type AND mode <=> @mode ORDER BY started_at DESC, id LIMIT 1",
            new { type = type.ToString(), mode = mode?.ToString() }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> AbortRunningAsync(DateTime finishedAt, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM jobs WHERE status = 'Running' FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken))).AsList();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE jobs SET status = 'Aborted', finished_at = @finishedAt WHERE status = 'Running'",
            new { finishedAt }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ids;
    }
}

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

    public async Task<Page<JobItem>> ListAsync(Guid jobId, string? outcome, int page, int pageSize, CancellationToken cancellationToken)
    {
        const string where = "WHERE job_id = @jobId AND (@outcome IS NULL OR outcome = @outcome)";
        var args = new { jobId, outcome, offset = Page<JobItem>.Offset(page, pageSize), pageSize };
        await using var connection = connections.Create();
        var total = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition($"SELECT COUNT(*) FROM job_items {where}", args, cancellationToken: cancellationToken));
        var items = await connection.QueryAsync<JobItem>(new CommandDefinition($"""
            SELECT id AS Id, job_id AS JobId, file_id AS FileId, system_id AS SystemId, source_path AS SourcePath,
                   archive_entry AS ArchiveEntry, outcome AS Outcome, reason AS Reason, details AS Details,
                   game_id AS GameId, sha1 AS Sha1, created_at AS CreatedAt
            FROM job_items {where} ORDER BY id LIMIT @pageSize OFFSET @offset
            """, args, cancellationToken: cancellationToken));
        return new Page<JobItem>(items.AsList(), page, pageSize, total);
    }
}
