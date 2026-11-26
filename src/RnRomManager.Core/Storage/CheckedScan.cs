namespace RnRomManager.Core.Storage;

public sealed record CheckedScan(IReadOnlyList<CheckedFile> Files, IReadOnlyList<CheckedScanProblem> Problems);
