using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Ingest;

/// <summary>The checked file an external ROM is hashed from and moved from (I2, I4).</summary>
/// <param name="Root">The storage root it was checked under: ingest, or the cache for an extracted archive entry.</param>
/// <param name="Progress">The job's progress: the moves of this file report <c>Copying</c> steps to it; null from Review.</param>
internal sealed record ExternalSource(
    string Root,
    string Path,
    ExternalFileSnapshot Snapshot,
    JobProgress? Progress = null);
