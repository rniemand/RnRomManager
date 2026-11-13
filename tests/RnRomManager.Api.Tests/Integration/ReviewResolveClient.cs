using System.Net;
using System.Text.Json;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// What <c>POST /api/review/{id}/resolve</c> came to (B1, B5): a synchronous refusal (<see cref="Status"/> not 202, its
/// problem <c>code</c>), or the finished job's summary <c>code</c>, <c>gameId</c> and <c>errors</c>.
/// </summary>
public sealed record ResolveOutcome(HttpStatusCode Status, string? Code, Guid? GameId, JsonElement? Errors, Guid? JobId);

public static class ReviewResolveClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Posts a resolve and, on <c>202</c>, waits for its job to end.</summary>
    public static async Task<ResolveOutcome> ResolveAsync(this AuthClient client, long id, object body)
    {
        Guid jobId;
        using (var response = await client.PostAsync($"/api/review/{id}/resolve", body))
        {
            var json = await AuthClient.JsonAsync(response);
            if (response.StatusCode != HttpStatusCode.Accepted)
                return new(response.StatusCode, json.GetProperty("code").GetString(), null,
                    json.TryGetProperty("errors", out var problemErrors) ? problemErrors : null, null);
            jobId = json.GetProperty("jobId").GetGuid();
        }
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            using var job = await client.GetAsync($"/api/jobs/{jobId}");
            var row = await AuthClient.JsonAsync(job);
            using var current = await client.GetAsync("/api/jobs/current");
            // The row is final before the runner releases the lock; wait for both, so the next action isn't JobRunning.
            if (row.GetProperty("status").GetString() != "Running" && current.StatusCode == HttpStatusCode.NoContent)
            {
                var summary = row.GetProperty("summary");
                if (summary.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"Job {jobId} ended {row.GetProperty("status")} without a summary: {row.GetProperty("error")}");
                var errors = summary.GetProperty("errors");
                return new(HttpStatusCode.Accepted, summary.GetProperty("code").GetString(),
                    summary.GetProperty("gameId").ValueKind == JsonValueKind.Null ? null : summary.GetProperty("gameId").GetGuid(),
                    errors.ValueKind == JsonValueKind.Null ? null : errors, jobId);
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Job {jobId} still running after {Timeout}");
            await Task.Delay(50);
        }
    }
}
