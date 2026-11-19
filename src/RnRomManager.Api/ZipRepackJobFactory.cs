using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;

namespace RnRomManager.Api;

/// <summary>A new ZIP repack job (Z9) for <paramref name="systemId"/>; <paramref name="level"/> is saved first, null for Repack again.</summary>
public delegate IJob ZipRepackJobFactory(string systemId, ZipCompression? level);
