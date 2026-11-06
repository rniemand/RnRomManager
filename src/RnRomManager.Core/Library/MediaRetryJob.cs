using Microsoft.Extensions.Logging;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;

namespace RnRomManager.Core.Library;

/// <summary>
/// "Retry missing media" (spec §10.2, D38): the per-game lookup for every game with the default box art, no box art or
/// no snap, ordered by title. Games where every release is <c>Manual</c> are skipped; a game is rewritten only when
/// something new was found; a per-game error is logged and the job continues. No <c>job_items</c>. When the circuit
/// breaker trips, the job stops and ends <c>Failed</c>.
/// </summary>
public sealed class MediaRetryJob(IGameRepository games, GameMediaRetry retry, ILogger<MediaRetryJob> logger) : IJob
{
    public JobType Type => JobType.MediaRetry;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var candidates = await games.ListMediaRetryCandidatesAsync(cancellationToken).ConfigureAwait(false);
        context.Progress.SetTotal(candidates.Count);
        var breaker = new CircuitBreaker();
        int updated = 0, notFound = 0, skipped = 0, error = 0;

        foreach (var game in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            context.Progress.SetCurrentItem(game.ZipPath);
            MediaRetryResult result;
            try
            {
                result = await retry.RetryAsync(game, breaker, skipManualOnly: true, context.Actor, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new(MediaRetryOutcome.Error, "Unexpected", ex.Message);
            }

            switch (result.Outcome)
            {
                case MediaRetryOutcome.Updated: updated++; break;
                case MediaRetryOutcome.NotFound: notFound++; break;
                case MediaRetryOutcome.Skipped: skipped++; break;
                default:
                    error++;
                    logger.LogError("Media retry for {ZipPath} failed: {Code} {Details}", game.ZipPath, result.Code, result.Details);
                    break;
            }
            context.Progress.ItemDone();

            if (breaker.IsOpen)
            {
                logger.LogWarning("Media retry stopped after {Done} of {Total} games: updated {Updated}, not found {NotFound}, skipped {Skipped}, error {Error}",
                    context.Progress.Current, candidates.Count, updated, notFound, skipped, error);
                throw new ThumbnailHostUnreachableException();
            }
        }
        return new { updated, notFound, skipped, error };
    }
}
