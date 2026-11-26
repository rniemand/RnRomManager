using RnRomManager.Core.Ingest;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/ingest/delete</c> body (D2).</summary>
public sealed record IngestDeleteRequest(IReadOnlyList<IngestDeleteRef?>? Files);
