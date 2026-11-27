using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Library;

/// <summary>
/// SG-1000 S12, modelled on <see cref="SaveFoldersJob"/>: moves every Master System game whose releases were all dropped as
/// <c>.sg</c> to SG-1000 (<see cref="LibraryEditor.MoveToSg1000Async"/>), one item per game, cancel between games. No setting:
/// the starter re-checks the candidates on every start.
/// </summary>
public sealed class Sg1000MoveJob(
    IGameRepository games,
    LibraryEditor editor,
    IJobItemRepository items,
    TimeProvider time,
    ILogger<Sg1000MoveJob> logger) : IJob
{
    public const string Moved = "Moved";
    public const string Skipped = "Skipped";
    public const string Error = "Error";

    public JobType Type => JobType.Sg1000Move;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var rows = await games.ListByOriginalExtensionAsync(Sg1000Mover.FromSystemId, Sg1000Mover.Extension, cancellationToken).ConfigureAwait(false);
        context.Progress.SetTotal(rows.Count);
        int moved = 0, skipped = 0, error = 0;

        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            context.Progress.SetCurrentItem(row.ZipPath);
            (string Outcome, string? Code, string? Details) result;
            try
            {
                // Not cancellable once a game starts: a ZIP is never half-written.
                var edit = await editor.MoveToSg1000Async(row, context.Actor, CancellationToken.None).ConfigureAwait(false);
                result = edit.Code switch
                {
                    null => (Moved, null, null),
                    LibraryEditResult.NotSg1000 => (Skipped, edit.Code, edit.Details),
                    _ => (Error, edit.Code, edit.Details),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Moving {ZipPath} to SG-1000 failed", row.ZipPath);
                result = (Error, "Unexpected", ex.Message);
            }

            switch (result.Outcome)
            {
                case Moved:
                    moved++;
                    break;
                case Skipped:
                    skipped++;
                    break;
                default:
                    error++;
                    logger.LogError("Moving {ZipPath} to SG-1000 failed: {Code} {Details}", row.ZipPath, result.Code, result.Details);
                    break;
            }
            await items.InsertAsync(new JobItem
            {
                JobId = context.JobId,
                FileId = row.Id,
                GameId = row.Id,
                SystemId = row.SystemId,
                SourcePath = AuditPaths.Library(row.ZipPath),
                Outcome = result.Outcome,
                Reason = result.Code,
                Details = result.Details,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            }, CancellationToken.None).ConfigureAwait(false);
            context.Progress.ItemDone();
        }
        return new { moved, skipped, error };
    }
}
