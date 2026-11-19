namespace RnRomManager.Core.Library;

/// <summary>
/// Z1: a system's ZIP compression level. <see cref="Med"/> is 0 so <c>default(ZipCompression)</c> is today's rule
/// (an unconfigured substitute <c>IRomStorageSettings</c> returns it).
/// </summary>
public enum ZipCompression
{
    /// <summary>Optimal, or Fastest for a <c>roms/</c> or <c>saves/</c> entry over the fast threshold.</summary>
    Med = 0,

    /// <summary>No compression (the Stored method) for every entry.</summary>
    Store = 1,

    /// <summary>SmallestSize for every entry.</summary>
    High = 2,
}
