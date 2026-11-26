namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/ingest/restore</c> body (D5).</summary>
public sealed record IngestRestoreRequest(IReadOnlyList<IngestRestoreRef?>? Files);
