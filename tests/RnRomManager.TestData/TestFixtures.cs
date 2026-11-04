namespace RnRomManager.TestData;

/// <summary>Committed binary fixtures (spec §15.2) and the builder output they must contain. See <c>Fixtures/README.md</c>.</summary>
public static class TestFixtures
{
    /// <summary>The one entry name inside <c>Fixtures/basic.7z</c>.</summary>
    public const string Basic7zEntry = "Seven Zip Demo (World).gb";

    /// <summary>The ROM inside <c>Fixtures/basic.7z</c>: rebuilt from a fixed seed, so DAT hashes stay valid.</summary>
    public static byte[] Basic7zRom() => new SyntheticRomBuilder { Seed = 7007, Title = "SEVENZIP" }.Build();

    /// <summary>Absolute path of <c>Fixtures/basic.7z</c> (copied next to the test assembly).</summary>
    public static string Basic7zPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "basic.7z");
}
