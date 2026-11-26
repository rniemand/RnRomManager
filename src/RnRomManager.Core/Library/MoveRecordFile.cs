namespace RnRomManager.Core.Library;

/// <summary>B18: one set file of a moved release, before and after the move.</summary>
public sealed record MoveRecordFile(string SourceExternalFileName, string TargetExternalFileName);
