using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Data.Repositories;

/// <summary><c>ingest_review_items</c> (spec §14.1). Grows with the review steps.</summary>
public sealed class IngestReviewRepository(IDbConnectionFactory connections) : IIngestReviewRepository
{
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
