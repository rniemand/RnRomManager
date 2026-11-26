namespace RnRomManager.Core.Ingest;

/// <summary>The whole archive is rejected (§7.3): a <c>Failed</c> reason code plus details.</summary>
public sealed class ArchiveRejectedException(string reason, string details) : Exception(details)
{
    public string Reason { get; } = reason;
}
