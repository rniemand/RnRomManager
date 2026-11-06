using RnRomManager.Core.Audit;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Media;

namespace RnRomManager.TestData;

/// <summary>Seeding shortcut for tests that ingest through the pipeline directly: recorded as a job with no user.</summary>
public static class IngestPipelineTestExtensions
{
    public static Task<IngestItemResult?> ProcessAsync(this IngestPipeline pipeline, IngestFile file, Guid jobId, CircuitBreaker breaker,
        CancellationToken cancellationToken) =>
        pipeline.ProcessAsync(file, jobId, new AuditActor(null, null, jobId), breaker, cancellationToken);
}
