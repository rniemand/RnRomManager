namespace RnRomManager.Core.Bundles;

/// <summary>A set-file member as the client sent it.</summary>
public sealed record BundleMemberInput(string? Sha1, string? FileName);
