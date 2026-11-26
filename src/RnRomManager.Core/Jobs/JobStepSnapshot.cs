namespace RnRomManager.Core.Jobs;

/// <summary>One streamed phase of the current item (<c>step</c> in <c>GET /api/jobs/current</c>).</summary>
public sealed record JobStepSnapshot(
    string Phase,
    long BytesDone,
    long BytesTotal,
    long? BytesPerSecond);
