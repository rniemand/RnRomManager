using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;

namespace RnRomManager.Core.Ingest;

internal sealed record Run(JobContext Context, Dictionary<string, int> Counts, CircuitBreaker Breaker);
