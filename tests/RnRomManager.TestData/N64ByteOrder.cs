namespace RnRomManager.TestData;

/// <summary>The byte order <see cref="SyntheticN64RomBuilder"/> writes (TestData doesn't reference the Systems project).</summary>
public enum N64ByteOrder
{
    /// <summary>Big-endian (<c>.z64</c>).</summary>
    Z64,

    /// <summary>Byte-swapped 16-bit pairs (<c>.v64</c>).</summary>
    V64,

    /// <summary>Little-endian 32-bit words (<c>.n64</c>).</summary>
    N64,
}
