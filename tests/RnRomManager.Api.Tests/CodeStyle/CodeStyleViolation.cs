namespace RnRomManager.Api.Tests.CodeStyle;

/// <summary>One broken style rule (S2) in one repo-relative file; formats as a baseline line.</summary>
public sealed record CodeStyleViolation(string Rule, string Path)
{
    public override string ToString() => $"{Rule} {Path}";
}
