namespace RnRomManager.Core.Jobs;

internal sealed record StepState(string Phase, long BytesTotal, DateTime StartedAtUtc);
