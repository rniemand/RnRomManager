namespace RnRomManager.Core.Bundles;

/// <summary>A separately downloadable set file within a release: its SHA-1 and stored <c>rom_files.file_name</c> (B3).</summary>
public sealed record BundleMember(string Sha1, string FileName);
