namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/jobs/ingest</c> body (IN1): the pending files to ingest, by system id and top-level file name.</summary>
public sealed record IngestRequest(IReadOnlyList<IngestFileRef?>? Files);
